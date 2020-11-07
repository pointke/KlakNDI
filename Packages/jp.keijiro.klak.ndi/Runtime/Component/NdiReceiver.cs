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
	}

	#endregion

	#region Component state controller

	internal void Restart() => ReleaseInternalObjects();

	#endregion

	#region MonoBehaviour implementation

	private CancellationTokenSource tokenSource;
	private CancellationToken cancellationToken;
	private static SynchronizationContext mainThreadContext;
	private AudioClip audioClip;

	void Awake()
	{
		mainThreadContext = SynchronizationContext.Current;

		if (_override == null) _override = new MaterialPropertyBlock();

		tokenSource = new CancellationTokenSource();
		cancellationToken = tokenSource.Token;

		Task.Run(ReceiveFrameTask, cancellationToken);
	}

	void OnDestroy()
	{
		tokenSource?.Cancel();
	}

	#endregion

	#region Receiver implementation

	void ReceiveFrameTask()
	{
		try
		{
			Debug.Log("Starting Task");

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

			if (cancellationToken.IsCancellationRequested)
			{
				Debug.Log("ReceiveFrameTask cancel requested.");
			}
		}
		catch (System.Exception e)
		{
			Debug.LogException(e);
		}
		finally
		{
			ReleaseInternalObjects();
			Debug.Log("Good night.");
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

		if (audioSource == null || !audioSource.enabled || !audioFrame.HasData)
		{
			_recv.FreeAudioFrame(audioFrame);
			return;
		}

		PrepareAudioSource(audioFrame);

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

	private readonly object audioBufferLock = new object();
	private CircularBuffer<float> audioBuffer;
	private const int BUFFER_SIZE = 4096;

	void PrepareAudioSource(Interop.AudioFrame audioFrame)
	{
		if (audioSource.isPlaying)
		{
			return;
		}

		// if the audio format changed, we need to create a new audio clip
		if (audioClip == null ||
			audioClip.channels != audioFrame.NoChannels ||
			audioClip.frequency != audioFrame.SampleRate)
		{
			Debug.Log($"PrepareAudioSource: Creating audio clip to match frame data: {audioFrame}");

			// Create a AudioClip that matches the incomming frame
			audioClip = AudioClip.Create("NdiReceiver Audio", audioFrame.SampleRate, audioFrame.NoChannels, audioFrame.SampleRate, true);

			lock (audioBufferLock)
			{
				if (audioBuffer == null || audioBuffer.Capacity != audioFrame.SampleRate)
				{
					audioBuffer = new CircularBuffer<float>(BUFFER_SIZE * audioFrame.NoChannels);
				}
			}
		}

		audioSource.loop = true;
		audioSource.clip = audioClip;
		audioSource.Play();
	}

	void OnAudioFilterRead(float[] data, int channels)
	{
		lock (audioBufferLock)
		{
			int length = data.Length;

			for (int i = 0; i < length; i++)
			{
				if (audioBuffer.IsEmpty)
				{
					data[i] = 0.0f;
				}
				else
				{
					data[i] = audioBuffer.Front();
					audioBuffer.PopFront();
				}
			}
		}
	}

	void FillAudioBuffer(Interop.AudioFrame audio)
	{
		if (_recv == null)
		{
			return;
		}

		lock (audioBufferLock)
		{
			if (audioBuffer == null)
			{
				audioBuffer = new CircularBuffer<float>(BUFFER_SIZE * audio.NoChannels);
			}

			// Converted from NDI C# Managed sample code
			// we're working in bytes, so take the size of a 32 bit sample (float) into account
			int sizeInBytes = audio.NoSamples * audio.NoChannels * sizeof(float);

			// Unity is expecting interleaved audio and NDI uses planar.
			// create an interleaved frame and convert from the one we received
			Interop.AudioFrameInterleaved interleavedAudio = new Interop.AudioFrameInterleaved()
			{
				SampleRate = audio.SampleRate,
				NoChannels = audio.NoChannels,
				NoSamples = audio.NoSamples,
				Timecode = audio.Timecode
			};

			// allocate native array to copy interleaved data into
			unsafe
			{
				using (var nativeArray = new NativeArray<byte>(sizeInBytes, Allocator.TempJob, NativeArrayOptions.UninitializedMemory))
				{
					interleavedAudio.Data = (IntPtr)nativeArray.GetUnsafePtr();

					// Convert from float planar to float interleaved audio
					_recv.AudioFrameToInterleaved(ref audio, ref interleavedAudio);

					var totalSamples = interleavedAudio.NoSamples * interleavedAudio.NoChannels;
					void* audioDataPtr = interleavedAudio.Data.ToPointer();

					for (int i = 0; i < totalSamples; i++)
					{
						audioBuffer.PushBack(UnsafeUtility.ReadArrayElement<float>(audioDataPtr, i));
					}
				}
			}
		}
	}

	#endregion

	}

}
