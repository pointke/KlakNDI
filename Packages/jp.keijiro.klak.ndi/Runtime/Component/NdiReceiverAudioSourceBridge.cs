using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Klak.Ndi
{
	[RequireComponent(typeof(AudioSource))]
	public class NdiReceiverAudioSourceBridge : MonoBehaviour
	{
		public delegate void HandleAudioFilterRead(float[] data, int channels);

		public HandleAudioFilterRead Handler;

		private void Awake()
		{
			hideFlags = HideFlags.NotEditable;
		}

		// Automagically called by Unity when an AudioSource component is present on the same GameObject
		private void OnAudioFilterRead(float[] data, int channels)
		{
			Handler(data, channels);
		}
	}
}