using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Klak.Ndi
{
	public sealed partial class NdiReceiver : MonoBehaviour
	{
		[RequireComponent(typeof(AudioSource))]
		private class AudioSourceBridge : MonoBehaviour
		{
			public bool IsDestroyed { get; internal set; }

			public NdiReceiver Handler { get; set; }

			private void Awake()
			{
				hideFlags = HideFlags.NotEditable;

				// Workaround for external AudioSources: Stop playback because otherwise volume and all it's other properties do not get applied.
				AudioSource audioSource = GetComponent<AudioSource>();
				audioSource.Stop();
				audioSource.Play();
			}

			// Automagically called by Unity when an AudioSource component is present on the same GameObject
			private void OnAudioFilterRead(float[] data, int channels)
			{
				Handler.HandleAudioFilterRead(data, channels);
			}

			private void OnDestroy()
			{
				if (IsDestroyed)
					return;

				IsDestroyed = true;

				Handler?.HandleAudioSourceBridgeOnDestroy();
			}
		}
	}
}