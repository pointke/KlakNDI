using System.Threading;
using System.Threading.Tasks;
using CircularBuffer;
using UnityEngine;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using IntPtr = System.IntPtr;

namespace Klak.Ndi {

// FIXME: re-enable the execute in edit mode (with on/off/mute toggle?)
//[ExecuteInEditMode]
public sealed partial class NdiReceiver : MonoBehaviour
{
	#region Internal objects

	Interop.Recv _recv;
	FormatConverter _converter;
	MaterialPropertyBlock _override;

	void PrepareInternalObjects()
	{
		if (_recv == null) _recv = RecvHelper.TryCreateRecv(_ndiName);
		if (_converter == null) _converter = new FormatConverter(_resources);
	}

	void ReleaseInternalObjects()
	{
		_recv?.Dispose();
		_recv = null;

		_converter?.Dispose();
		_converter = null;

		if(m_aTempAudioPullBuffer.IsCreated)
			m_aTempAudioPullBuffer.Dispose();
	}

	#endregion

	#region Component state controller

	internal void Restart() => ReleaseInternalObjects();

	#endregion

	#region MonoBehaviour implementation

	private CancellationTokenSource tokenSource;
	private CancellationToken cancellationToken;
	private static SynchronizationContext mainThreadContext;

	void Awake()
	{
		mainThreadContext = SynchronizationContext.Current;

		if (_override == null) _override = new MaterialPropertyBlock();

		tokenSource = new CancellationTokenSource();
		cancellationToken = tokenSource.Token;

		Task.Run(ReceiveFrameTask, cancellationToken);

		UpdateAudioExpectations();
		AudioSettings.OnAudioConfigurationChanged += AudioSettings_OnAudioConfigurationChanged;
		CheckAudioSource();
	}

	void OnDestroy()
	{
		tokenSource?.Cancel();
		ReleaseInternalObjects();

		AudioSettings.OnAudioConfigurationChanged -= AudioSettings_OnAudioConfigurationChanged;
		DestroyAudioSourceBridge();
	}

	#endregion

	#region Receiver implementation

	void ReceiveFrameTask()
	{
		try
		{
			// retrieve frames in a loop
			while (!cancellationToken.IsCancellationRequested)
			{
				PrepareInternalObjects();

				if (_recv == null)
				{
					Thread.Sleep(100);
					continue;
				}

				Interop.VideoFrame video;
				Interop.AudioFrame audio;
				Interop.MetadataFrame metadata;

				var type = _recv.Capture(out video, out audio, out metadata, 5000);
				switch (type)
				{
					case Interop.FrameType.Audio:
						//Debug.Log($"received {type}: {audio}");
						FillAudioBuffer(audio);
						mainThreadContext.Post(ProcessAudioFrame, audio);
						break;
					case Interop.FrameType.Error:
						//Debug.Log($"received {type}: {video} {audio} {metadata}");
						mainThreadContext.Post(ProcessStatusChange, true);
						break;
					case Interop.FrameType.Metadata:
						//Debug.Log($"received {type}: {metadata}");
						mainThreadContext.Post(ProcessMetadataFrame, metadata);
						break;
					case Interop.FrameType.None:
						//Debug.Log($"received {type}");
						break;
					case Interop.FrameType.StatusChange:
						//Debug.Log($"received {type}: {video} {audio} {metadata}");
						mainThreadContext.Post(ProcessStatusChange, false);
						break;
					case Interop.FrameType.Video:
						//Debug.Log($"received {type}: {video}");
						mainThreadContext.Post(ProcessVideoFrame, video);
						break;
				}
			}
		}
		catch (System.Exception e)
		{
			Debug.LogException(e);
		}
	}

	void ProcessVideoFrame(System.Object data)
	{
		Interop.VideoFrame videoFrame = (Interop.VideoFrame)data;

		if (_recv == null) return;

		// Pixel format conversion
		var rt = _converter.Decode
			(videoFrame.Width, videoFrame.Height,
			Util.CheckAlpha(videoFrame.FourCC), videoFrame.Data);

		// Copy the metadata if any.
		metadata = videoFrame.Metadata;

		// Free the frame up.
		_recv.FreeVideoFrame(videoFrame);

		if (rt == null) return;

		// Material property override
		if (_targetRenderer != null)
		{
			_targetRenderer.GetPropertyBlock(_override);
			_override.SetTexture(_targetMaterialProperty, rt);
			_targetRenderer.SetPropertyBlock(_override);
		}

		// External texture update
		if (_targetTexture != null)
			Graphics.Blit(rt, _targetTexture);
	}

	void ProcessAudioFrame(System.Object data)
	{
		Interop.AudioFrame audioFrame = (Interop.AudioFrame)data;

		if (_recv == null) return;

		_recv.FreeAudioFrame(audioFrame);
	}


	void ProcessMetadataFrame(System.Object data)
	{
		Interop.MetadataFrame metadataFrame = (Interop.MetadataFrame)data;

		if (_recv == null) return;

		// broadcast an event that new metadata has arrived?

		Debug.Log($"ProcessMetadataFrame: {metadataFrame.Data}");

		_recv.FreeMetadataFrame(metadataFrame);
	}

	void ProcessStatusChange(System.Object data)
	{
		bool error = (bool)data;

		// broadcast an event that we've received/lost stream?

		Debug.Log($"ProcessStatusChange error = {error}");
	}

	#endregion

	#region Audio implementation

	private readonly object					audioBufferLock = new object();
	private const int						BUFFER_SIZE = 1024 * 32;
	private CircularBuffer<float>			audioBuffer = new CircularBuffer<float>(BUFFER_SIZE);
	//
	private bool							m_bWaitForBufferFill = true;
	private const int						m_iMinBufferAheadFrames = 4;
	//
	private NativeArray<byte>				m_aTempAudioPullBuffer;
	private Interop.AudioFrameInterleaved	interleavedAudio = new Interop.AudioFrameInterleaved();
	//
	private float[]							m_aTempSamplesArray = new float[ 1024 * 32 ];
	
	private int _expectedAudioSampleRate;
	private int _expectedAudioChannels;

	private int _receivedAudioSampleRate;
	private int _receivedAudioChannels;

	private bool _hasAudioSource;
	private NdiReceiverAudioSourceBridge _audioSourceBridge;

	internal void CheckAudioSource()
	{
		if(Application.isPlaying == false)
			return;

		_hasAudioSource = _audioSource != null;

		DestroyAudioSourceBridge();

		if (_hasAudioSource == false)
			return;

		// Make sure it is playing so OnAudioFilterRead gets called by Unity
		_audioSource.Play();

		if (_audioSource.gameObject == gameObject)
			return;

		// Create a bridge component if the AudioSource is not on this GameObject so we can feed audio samples to it.
		_audioSourceBridge = _audioSource.GetComponent<NdiReceiverAudioSourceBridge>();
		if(_audioSourceBridge == null)
			_audioSourceBridge = _audioSource.gameObject.AddComponent<NdiReceiverAudioSourceBridge>();

		_audioSourceBridge.Handler = this;
	}

	private void DestroyAudioSourceBridge()
	{
		if (_audioSourceBridge == null)
			return;

		_audioSourceBridge.Handler = null;

		if(_audioSourceBridge.IsDestroyed == false)
			GameObject.DestroyImmediate(_audioSourceBridge);

		_audioSourceBridge = null;
	}

	private void AudioSettings_OnAudioConfigurationChanged(bool deviceWasChanged)
	{
		UpdateAudioExpectations();
	}

	private void UpdateAudioExpectations()
	{
		_expectedAudioSampleRate = AudioSettings.outputSampleRate;
		switch (AudioSettings.speakerMode)
		{
			case AudioSpeakerMode.Mono:
			case AudioSpeakerMode.Stereo:
				_expectedAudioChannels = (int)AudioSettings.speakerMode;
				break;

			case AudioSpeakerMode.Quad:
				_expectedAudioChannels = 4;
				break;

			case AudioSpeakerMode.Surround:
				_expectedAudioChannels = 5;
				break;

			case AudioSpeakerMode.Mode5point1:
				_expectedAudioChannels = 6;
				break;

			case AudioSpeakerMode.Mode7point1:
				_expectedAudioChannels = 8;
				break;
		}
	}

	// Automagically called by Unity when an AudioSource component is present on the same GameObject
	void OnAudioFilterRead(float[] data, int channels)
	{
		if ((object)_audioSource == null)
			return;

		if ((object)_audioSourceBridge != null)
			return;

		HandleAudioFilterRead(data, channels);
	}

	internal void HandleAudioSourceBridgeOnDestroy()
	{
		_audioSource = null;

		DestroyAudioSourceBridge();
	}

	internal void HandleAudioFilterRead(float[] data, int channels)
	{
		int length = data.Length;

		// STE: Waiting for enough read ahead buffer frames?
		if (m_bWaitForBufferFill)
		{
			// Are we good yet?
			// Should we be protecting audioBuffer.Size here?
			m_bWaitForBufferFill = ( audioBuffer.Size < (length * m_iMinBufferAheadFrames) );

			// Early out if not enough in the buffer still
			if (m_bWaitForBufferFill)
			{
				return;
			}
		}

		bool bPreviousWaitForBufferFill = m_bWaitForBufferFill;
		int iAudioBufferSize = 0;

		// STE: Lock buffer for the smallest amount of time
		lock (audioBufferLock)
		{
			iAudioBufferSize = audioBuffer.Size;

			// If we do not have enough data for a single frame then we will want to buffer up some read-ahead audio data. This will cause a longer gap in the audio playback, but this is better than more intermittent glitches I think
			m_bWaitForBufferFill = (iAudioBufferSize < length);
			if( !m_bWaitForBufferFill )
			{
				audioBuffer.Front( ref data, length );
				audioBuffer.PopFront( length );
			}
		}

		if ( m_bWaitForBufferFill && !bPreviousWaitForBufferFill )
		{
			Debug.LogWarning($"Audio buffer underrun: OnAudioFilterRead: data.Length = {data.Length} | audioBuffer.Size = {iAudioBufferSize}", this);
		}
	}

	void FillAudioBuffer(Interop.AudioFrame audio)
	{
		if (_recv == null || _hasAudioSource == false)
		{
			return;
		}

		if (audio.SampleRate != _receivedAudioSampleRate)
		{
			_receivedAudioSampleRate = audio.SampleRate;
			if (_receivedAudioSampleRate != _expectedAudioSampleRate)
				Debug.LogWarning($"Audio sample rate does not match. Expected {_expectedAudioSampleRate} but received {_receivedAudioSampleRate}.", this);
		}

		if(audio.NoChannels != _receivedAudioChannels)
		{
			_receivedAudioChannels = audio.NoChannels;
			if(_receivedAudioChannels != _expectedAudioChannels)
				Debug.LogWarning($"Audio channel count does not match. Expected {_expectedAudioChannels} but received {_receivedAudioChannels}.", this);
		}

		if (audio.Metadata != null)
			Debug.Log(audio.Metadata);

		// Converted from NDI C# Managed sample code
		// we're working in bytes, so take the size of a 32 bit sample (float) into account
		int sizeInBytes = audio.NoSamples * audio.NoChannels * sizeof(float);

		// Unity is expecting interleaved audio and NDI uses planar.
		// create an interleaved frame and convert from the one we received
		interleavedAudio.SampleRate = audio.SampleRate;
		interleavedAudio.NoChannels = audio.NoChannels;
		interleavedAudio.NoSamples = audio.NoSamples;
		interleavedAudio.Timecode = audio.Timecode;

		// allocate native array to copy interleaved data into
		unsafe
		{
			if( m_aTempAudioPullBuffer.Length < sizeInBytes)
			{
				if (m_aTempAudioPullBuffer.IsCreated)
					m_aTempAudioPullBuffer.Dispose();

				m_aTempAudioPullBuffer = new NativeArray<byte>(sizeInBytes, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
			}

			interleavedAudio.Data = (IntPtr)m_aTempAudioPullBuffer.GetUnsafePtr();
			if ( interleavedAudio.Data != null )
			{
				// Convert from float planar to float interleaved audio
				_recv.AudioFrameToInterleaved(ref audio, ref interleavedAudio);

				var totalSamples = interleavedAudio.NoSamples * _expectedAudioChannels;
				void* audioDataPtr = interleavedAudio.Data.ToPointer();

				if (audioDataPtr != null)
				{
					// Grab data from native array
					if (m_aTempSamplesArray.Length < totalSamples)
					{
						m_aTempSamplesArray = new float[totalSamples];
					}

					// Blindly write a mix of all input channels to the output channels if their count does not match. Better make this behaviour opt-in?
					if (_receivedAudioChannels != _expectedAudioChannels)
					{
						for (int i = 0; i < interleavedAudio.NoSamples; i++)
						{
							var sample = 0f;
							for (int j = 0; j < interleavedAudio.NoChannels; j++)
								sample += UnsafeUtility.ReadArrayElement<float>(audioDataPtr, i * interleavedAudio.NoChannels + j);

							for (int j = 0; j < _expectedAudioChannels; j++)
								m_aTempSamplesArray[i * _expectedAudioChannels + j] = sample;
						}
					}
					else
					{
						var tempSamplesPtr = UnsafeUtility.PinGCArrayAndGetDataAddress(m_aTempSamplesArray, out ulong tempSamplesHandle);
						UnsafeUtility.MemCpy(tempSamplesPtr, audioDataPtr, totalSamples * sizeof(float));
						UnsafeUtility.ReleaseGCObject(tempSamplesHandle);
					}

					// Copy new sample data into the circular array
					lock (audioBufferLock)
					{
						if (audioBuffer.Capacity < totalSamples)
						{
							audioBuffer = new CircularBuffer<float>(totalSamples);
						}

						audioBuffer.PushBack(m_aTempSamplesArray, totalSamples);
						//audioBuffer.PushBack( m_aTempSamplesArray, Mathf.Min(audioBuffer.Capacity, totalSamples) );
					}
				}
			}
		}
	}

	#endregion

	}

}
