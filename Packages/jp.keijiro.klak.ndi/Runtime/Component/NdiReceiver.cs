using System.Threading;
using System.Threading.Tasks;
using CircularBuffer;
using UnityEngine;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using IntPtr = System.IntPtr;

namespace Klak.Ndi
{

    // FIXME: re-enable the execute in edit mode (with on/off/mute toggle?)
    //[ExecuteInEditMode]
    public sealed partial class NdiReceiver : MonoBehaviour
    {
        #region Internal objects

        Interop.Recv _recv;
        FormatConverter _converter;
        MaterialPropertyBlock _override;


        [ContextMenu("Reload Settings")]

        public void ReloadSettings()
        {
            SharedInstance.OnDomainReload();
        }
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
        
            if (audioClip != null)
            {
                GameObject.Destroy(audioClip);
            }
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

        #region Audio Public Methods

        public float Volume
        {
            get
            {
                return (audioSource != null) ? audioSource.volume : -1;
            }
            set
            {
                if (audioSource != null)
                    audioSource.volume = value;
            }
        }
        #endregion

        #region Audio implementation

        private readonly object audioBufferLock = new object();
        private const int BUFFER_SIZE = 1024 * 32;
        private CircularBuffer<float> audioBuffer = new CircularBuffer<float>(BUFFER_SIZE);
        //
        private bool m_bWaitForBufferFill = true;
        private const int m_iMinBufferAheadFrames = 4;
        //
        private NativeArray<byte> m_aTempAudioPullBuffer;
        private Interop.AudioFrameInterleaved interleavedAudio = new Interop.AudioFrameInterleaved();
        //
        private float[] m_aTempSamplesArray = new float[1024 * 32];

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
            }

            audioSource.loop = true;
            audioSource.clip = audioClip;
            audioSource.Play();
        }


        void OnAudioFilterRead(float[] data, int channels)
        {
            int length = data.Length;

            // STE: Waiting for enough read ahead buffer frames?
            if (m_bWaitForBufferFill)
            {
                // Are we good yet?
                // Should we be protecting audioBuffer.Size here?
                m_bWaitForBufferFill = (audioBuffer.Size < (length * m_iMinBufferAheadFrames));

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
                if (!m_bWaitForBufferFill)
                {
                    audioBuffer.Front(ref data, data.Length);
                    audioBuffer.PopFront(data.Length);
                }
            }

            if (m_bWaitForBufferFill && !bPreviousWaitForBufferFill)
            {
                Debug.Log("NOT ENOUGH AUDIO : OnAudioFilterRead: data.Length = " + data.Length + "| audioBuffer.Size = " + iAudioBufferSize);
            }
        }

        void FillAudioBuffer(Interop.AudioFrame audio)
        {
            if (_recv == null)
            {
                return;
            }

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
                if (m_aTempAudioPullBuffer == null || m_aTempAudioPullBuffer.Length < sizeInBytes)
                {
                    m_aTempAudioPullBuffer = new NativeArray<byte>(sizeInBytes, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                }

                interleavedAudio.Data = (IntPtr)m_aTempAudioPullBuffer.GetUnsafePtr();
                if (interleavedAudio.Data != null)
                {
                    // Convert from float planar to float interleaved audio
                    _recv.AudioFrameToInterleaved(ref audio, ref interleavedAudio);

                    var totalSamples = interleavedAudio.NoSamples * interleavedAudio.NoChannels;
                    void* audioDataPtr = interleavedAudio.Data.ToPointer();

                    if (audioDataPtr != null)
                    {
                        // Grab data from native array
                        if (m_aTempSamplesArray == null || m_aTempSamplesArray.Length < totalSamples)
                        {
                            m_aTempSamplesArray = new float[totalSamples];
                        }
                        if (m_aTempSamplesArray != null)
                        {
                            for (int i = 0; i < totalSamples; i++)
                            {
                                m_aTempSamplesArray[i] = UnsafeUtility.ReadArrayElement<float>(audioDataPtr, i);
                            }
                        }

                        // Copy new sample data into the circular array
                        lock (audioBufferLock)
                        {
                            audioBuffer.PushBack(m_aTempSamplesArray, totalSamples);
                        }
                    }
                }
            }
        }

        #endregion

    }

}
