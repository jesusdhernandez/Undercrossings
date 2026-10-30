using UnityEngine;

// AudioSource/AudioListener volume is capped at 1. This DSP stage permits the requested boost above normal.
public sealed class AudioGain : MonoBehaviour
{
    // Written on the main thread and read by Unity's audio thread; no Unity APIs run in the callback.
    public volatile float amplification = 1;

    void OnAudioFilterRead(float[] data, int channels)
    {
        float gain = amplification;
        if (gain == 1)
            return;

        // Apply the same gain to every interleaved channel, preserving stereo and spatial direction.
        for (int i = 0; i < data.Length; i++)
            data[i] *= gain;
    }
}
