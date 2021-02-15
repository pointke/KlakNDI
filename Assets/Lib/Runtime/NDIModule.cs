using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Klak.Ndi;
using UnityEngine.Audio;
using Klak.Ndi.Interop;
using System;
using System.IO;
//Wrapper to wrap some NDI functionality into our module system to make it easier to integrate into our applications.
namespace RenderHeads.NDI
{

    public class NDIData
    {
        public NdiReceiver Receiver;
        public AudioSource AudioSource;
    }
    public class NDIModule : INDIModule
    {
        private Dictionary<RenderTexture, NDIData> _ndiComponents = new Dictionary<RenderTexture, NDIData>();
        private GameObject _ndiGO;


        public NDIModule(bool useLocalSources, List<string> externalIPS)
        {
            SerializedNDISettings settings = new SerializedNDISettings()
            {
                UseLocalSources = useLocalSources,
                ExternalIps = externalIPS

            };
            //these settings are later read back by the FIND interop.
            WriteSettingsToDisk(settings);
            _ndiGO = new GameObject("NDI COMPONENTS");
        }

        private void WriteSettingsToDisk(SerializedNDISettings settings)
        {
            if (!Directory.Exists(Application.streamingAssetsPath))
            {
                Directory.CreateDirectory(Application.streamingAssetsPath);
            }
            File.WriteAllText(Path.Combine(Application.streamingAssetsPath, "NDISettings.json"), JsonUtility.ToJson(settings));

        }

        public RenderTexture CaptureStream(string streamName, int targetWidth, int targetHeight, AudioMixerGroup mixerGroup = null)
        {
            NdiReceiver recv = _ndiGO.AddComponent<NdiReceiver>();
            RenderTexture rt = RenderTexture.GetTemporary(targetWidth, targetHeight);
            recv.targetTexture = rt;
            recv.ndiName = streamName;
            AudioSource a = _ndiGO.AddComponent<AudioSource>();
            a.outputAudioMixerGroup = mixerGroup;
            _ndiComponents.Add(rt, new NDIData() { Receiver = recv, AudioSource = a });
            return rt;

        }

        public float GetVolume(RenderTexture streamTexture)
        {
            if (_ndiComponents.ContainsKey(streamTexture))
            {
                return _ndiComponents[streamTexture].AudioSource.volume;
            }
            else
            {
                return -1;
            }
        }

        public void ReleaseStream(RenderTexture streamTexture)
        {
            if (_ndiComponents.ContainsKey(streamTexture))
            {
                NDIData data = _ndiComponents[streamTexture];
                _ndiComponents.Remove(streamTexture);
                GameObject.Destroy(data.Receiver);
                GameObject.Destroy(data.AudioSource);
                data = null;
                RenderTexture.ReleaseTemporary(streamTexture);
            }
            else
            {
                throw new System.Exception("steamTexture key not found, maybe already removed?");
            }
        }

        public void SetVolume(RenderTexture streamTexture, float Volume)
        {
            if (_ndiComponents.ContainsKey(streamTexture))
            {
                _ndiComponents[streamTexture].AudioSource.volume = Volume;
            }
        }

        public void UpdateModule()
        {
            //we don't need to update anything so leave this blank
        }
    }
}