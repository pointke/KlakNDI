using RenderHeads.Tooling.Core.ModulePattern;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Audio;

namespace RenderHeads.NDI
{

    public interface INDIModule : IModule
    {
        /// <summary>
        /// Find and start a stream with a given name
        /// </summary>
        /// <param name="streamName">the name of the stream to load</param>
        /// <param name="targetWidth">The width of the texture to be generated</param>
        /// <param name="targetHeight">The height of the texture to be generated</param>
        RenderTexture CaptureStream(string streamName, int targetWidth, int targetHeight, AudioMixerGroup audioMixer = null);


        /// <summary>
        /// Stops and unloads a stream
        /// </summary>
        /// <param name="streamIndex">The index of the stream to unload. Will do nothing if the index is invalid</param>
        void ReleaseStream(RenderTexture streamTexture);

        /// <summary>
        /// Set the volume of a given stream
        /// </summary>
        /// <param name="streamTexture">the texture related to the stream</param>
        /// <param name="Volume">The volume to set between 0 - 1</param>
        void SetVolume(RenderTexture streamTexture, float Volume);


        /// <summary>
        /// Gets the volume of a stream
        /// </summary>
        /// <param name="streamTexture">the texture related to the stream</param>
        /// <returns>Volume of stream between 0 - 1, returns -1 if the stream index is not valid</returns>
        float GetVolume(RenderTexture streamTexture);




    }
}