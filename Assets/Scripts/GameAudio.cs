using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Serialization;

// Central sound routing keeps volume settings, pitch variation, and pause behavior consistent.
[DefaultExecutionOrder(-200)]
public sealed class GameAudio : MonoBehaviour
{
    // Cue order matches the scene's clip array. Several cues share one volume channel.
    public enum Cue
    {
        Ambiance,
        BrightonHit,
        BrightonDead,
        ButtonHover,
        ButtonPress,
        ButtonRelease,
        DarktonHit,
        ItemPickup,
        Place,
        Shoot,
        BrightonStep,
        DarktonStep
    }

    public static GameAudio Instance { get; private set; }
    public enum Channel
    {
        Ambiance,
        BrightonHit,
        BrightonDead,
        Buttons,
        DarktonHit,
        ItemPickup,
        Place,
        Shoot,
        Footsteps
    }
    public static readonly string[] Labels = { "Ambiance",        "Brighton hit", "Brighton death",
                                               "Buttons",         "Darkton hit",  "Item pickup",
                                               "Light placement", "Gunshot",      "Footsteps" };
    static float masterGain = 1;

    public AudioClip[] clips = new AudioClip[12];
    [FormerlySerializedAs("ambience")]
    public AudioClip[] ambiance = new AudioClip[3];
    [FormerlySerializedAs("ambienceInterval")]
    [Min(1)]
    public float ambianceInterval = 60;
    [FormerlySerializedAs("ambienceChance")]
    [Range(0, 1)]
    public float ambianceChance = 0.5f;
    public Vector2 pitchVariation = new Vector2(0.9f, 1.1f);
    [Min(1)]
    public float enemyHearingDistance = 12;

    // Pooled voices allow overlapping sounds without creating/destroying objects on every footstep.
    sealed class Voice
    {
        public AudioSource source;
        public AudioReverbFilter reverb;
        public AudioGain gain;
        public Cue cue;
        public bool paused, world;
        public float started;
    }

    readonly float[] volumes = new float[9];
    readonly Voice[] voices = new Voice[48];
    bool buttonDown;
    AudioListener listener;
    AudioListener[] previousListeners;
    bool[] previousListenerEnabled;
    WorldsidesGameplay game;
    float ambianceAge;
    AudioSource previewSource;
    AudioGain previewGain;
    Channel? previewChannel;
    bool previewHeld;

    void Awake()
    {
        Instance = this;

        // New grouped settings take priority; migrate old individual values without discarding preferences.
        for (int i = 0; i < volumes.Length; i++)
            volumes[i] =
                Mathf.Clamp01(PlayerPrefs.GetFloat("Settings.AudioGroup." + (Channel)i, LegacyVolume((Channel)i)));

        // Listen from Brighton instead of the elevated/peeking camera, so distance sounds natural.
        previousListeners = FindObjectsByType<AudioListener>(FindObjectsSortMode.None);
        previousListenerEnabled = new bool[previousListeners.Length];
        for (int i = 0; i < previousListeners.Length; i++)
        {
            previousListenerEnabled[i] = previousListeners[i].enabled;
            previousListeners[i].enabled = false;
        }
        var listenerObject = new GameObject("Player audio listener");
        listenerObject.transform.SetParent(transform, false);
        listener = listenerObject.AddComponent<AudioListener>();

        // Every voice has an optional reverb filter; only enemy footsteps enable it.
        for (int i = 0; i < voices.Length; i++)
        {
            var source = NewSource("Sound voice " + i);
            var reverb = source.gameObject.AddComponent<AudioReverbFilter>();
            reverb.enabled = false;
            voices[i] =
                new Voice { source = source, reverb = reverb, gain = source.gameObject.AddComponent<AudioGain>() };
        }

        // Previews have a dedicated non-spatial voice that is never paused with world sounds.
        previewSource = NewSource("Volume preview");
        previewGain = previewSource.gameObject.AddComponent<AudioGain>();
    }

    void Start()
    {
        game = FindAnyObjectByType<WorldsidesGameplay>();
    }

    AudioSource NewSource(string name)
    {
        var child = new GameObject(name);
        child.transform.SetParent(transform, false);
        var source = child.AddComponent<AudioSource>();
        source.playOnAwake = false;
        source.dopplerLevel = 0;
        return source;
    }

    void Update()
    {
        // World audio pauses with menus/death, but UI and Brighton's death sound keep playing.
        bool blocked = GameMenus.Blocking || (game && game.Dead);
        foreach (var voice in voices)
        {
            if (!voice.world)
                continue;
            if (blocked && !voice.paused && voice.source.isPlaying)
            {
                voice.source.Pause();
                voice.paused = true;
            }
            else if (!blocked && voice.paused)
            {
                voice.source.UnPause();
                voice.paused = false;
            }
        }

        // Update before sound playback so attenuation follows the player's current position.
        FollowPlayer();
        UpdateEnemyAcoustics();
        if (!blocked)
            AdvanceAmbiance(Time.deltaTime);

        // Stop repeating on release even if the pointer has left the slider or the app loses focus.
        if (previewHeld && (Mouse.current == null || !Mouse.current.leftButton.isPressed || !Application.isFocused))
            EndPreview();

        // Track release independently of clip playback: a one-shot may finish while the mouse stays down.
        if (buttonDown && (Mouse.current == null || !Mouse.current.leftButton.isPressed || !Application.isFocused))
        {
            buttonDown = false;
            if (Application.isFocused)
                Play(Cue.ButtonRelease);
        }
    }

    void LateUpdate()
    {
        FollowPlayer();
        UpdateEnemyAcoustics();
    }

    void FollowPlayer()
    {
        if (!game || !game.Player || !listener)
            return;
        listener.transform.position = game.PickupOrigin;

        // Horizontal camera orientation determines stereo left/right, without camera pitch or peek.
        var camera = Camera.main;
        if (camera)
        {
            Vector3 forward = camera.transform.forward;
            forward.y = 0;
            if (forward.sqrMagnitude > 0.001f)
                listener.transform.rotation = Quaternion.LookRotation(forward, Vector3.up);
        }
    }

    public void AdvanceAmbiance(float seconds)
    {
        // Roll once per active gameplay minute, never while sitting in a menu.
        ambianceAge += Mathf.Max(0, seconds);
        if (ambianceAge < ambianceInterval)
            return;
        ambianceAge = 0;
        if (Random.value < ambianceChance)
            Play(Cue.Ambiance);
    }

    public static Channel ChannelFor(Cue cue)
    {
        // Shared UI/footstep controls do not change which clip or spatial settings each cue uses.
        switch (cue)
        {
        case Cue.ButtonHover:
        case Cue.ButtonPress:
        case Cue.ButtonRelease:
            return Channel.Buttons;
        case Cue.BrightonStep:
        case Cue.DarktonStep:
            return Channel.Footsteps;
        case Cue.BrightonHit:
            return Channel.BrightonHit;
        case Cue.BrightonDead:
            return Channel.BrightonDead;
        case Cue.DarktonHit:
            return Channel.DarktonHit;
        case Cue.ItemPickup:
            return Channel.ItemPickup;
        case Cue.Place:
            return Channel.Place;
        case Cue.Shoot:
            return Channel.Shoot;
        default:
            return Channel.Ambiance;
        }
    }

    static float LegacyVolume(Channel channel)
    {
        float Old(Cue cue) => PlayerPrefs.GetFloat("Settings.Audio." + cue, 0.5f);
        switch (channel)
        {
        // Keep preferences saved under the old spelling when migrating the renamed channel.
        case Channel.Ambiance:
            return PlayerPrefs.GetFloat("Settings.AudioGroup.Ambience",
                                        PlayerPrefs.GetFloat("Settings.Audio.Ambience", 0.5f));
        // Average the previous controls when first consolidating them.
        case Channel.Buttons:
            return (Old(Cue.ButtonHover) + Old(Cue.ButtonPress) + Old(Cue.ButtonRelease)) / 3;
        case Channel.Footsteps:
            return (Old(Cue.BrightonStep) + Old(Cue.DarktonStep)) / 2;
        default:
            return Old((Cue)System.Enum.Parse(typeof(Cue), channel.ToString()));
        }
    }

    public float GetVolume(Channel channel) => volumes[(int)channel];
    public float GetVolume(Cue cue) => GetVolume(ChannelFor(cue));
    public void SetVolume(Cue cue, float value) => SetVolume(ChannelFor(cue), value);

    public void SetVolume(Channel channel, float value)
    {
        if (Mathf.Approximately(volumes[(int)channel], Mathf.Clamp01(value)))
            return;
        volumes[(int)channel] = Mathf.Clamp01(value);

        // Refresh both playing and paused voices so the grouped controls preview immediately.
        foreach (var voice in voices)
            if (ChannelFor(voice.cue) == channel)
                ApplyGain(voice);
        ApplyPreviewGain();
    }

    public void SaveVolumes()
    {
        for (int i = 0; i < volumes.Length; i++)
            PlayerPrefs.SetFloat("Settings.AudioGroup." + (Channel)i, volumes[i]);
    }

    // Below 50% attenuates logarithmically; 50% is unity gain; 100% doubles amplitude (+6 dB).
    public static float VolumeGain(float value)
    {
        value = Mathf.Clamp01(value);
        if (value <= 0)
            return 0;
        float decibels = value <= 0.5f ? Mathf.Lerp(-40, 0, value * 2) : Mathf.Lerp(0, 6.0206f, (value - 0.5f) * 2);
        return Mathf.Pow(10, decibels / 20);
    }

    public static void SetMasterVolume(float value)
    {
        masterGain = VolumeGain(value);

        // The listener handles attenuation globally; source DSP handles only the boost above 1.
        AudioListener.volume = Mathf.Min(1, masterGain);
        if (Instance)
        {
            foreach (var voice in Instance.voices)
                Instance.ApplyGain(voice);
            Instance.ApplyPreviewGain();
        }
    }

    // Artistic channel balance is separate from the logarithmic sliders, whose 50% stays unity gain.
    public static float ChannelBalance(Channel channel)
    {
        if (channel == Channel.Ambiance || channel == Channel.Shoot)
            return 2;
        return channel == Channel.Footsteps ? 0.5f : 1;
    }

    void ApplyGain(Voice voice)
    {
        voice.source.volume = 1;
        voice.gain.amplification =
            VolumeGain(GetVolume(voice.cue)) * ChannelBalance(ChannelFor(voice.cue)) * Mathf.Max(1, masterGain);
    }

    // A representative clip loops only while held. Master uses a neutral button clip without channel attenuation.
    public void BeginPreview(Channel? channel = null)
    {
        previewSource.Stop();
        previewChannel = channel;
        Cue cue = Cue.ButtonPress;
        if (channel.HasValue)
        {
            switch (channel.Value)
            {
            case Channel.Ambiance:
                cue = Cue.Ambiance;
                break;
            case Channel.BrightonHit:
                cue = Cue.BrightonHit;
                break;
            case Channel.BrightonDead:
                cue = Cue.BrightonDead;
                break;
            case Channel.DarktonHit:
                cue = Cue.DarktonHit;
                break;
            case Channel.ItemPickup:
                cue = Cue.ItemPickup;
                break;
            case Channel.Place:
                cue = Cue.Place;
                break;
            case Channel.Shoot:
                cue = Cue.Shoot;
                break;
            case Channel.Footsteps:
                cue = Cue.BrightonStep;
                break;
            }
        }
        previewSource.clip = cue == Cue.Ambiance
                                 ? (ambiance.Length > 0 ? ambiance[Random.Range(0, ambiance.Length)] : null)
                                 : clips[(int)cue];
        previewHeld = true;
        previewSource.loop = true;
        ApplyPreviewGain();
        if (previewSource.clip)
            previewSource.Play();
    }

    void ApplyPreviewGain()
    {
        if (!previewGain)
            return;
        float gain = previewChannel.HasValue
                         ? VolumeGain(GetVolume(previewChannel.Value)) * ChannelBalance(previewChannel.Value)
                         : 1;
        previewGain.amplification = gain * Mathf.Max(1, masterGain);
    }

    public void EndPreview()
    {
        previewHeld = false;
        previewSource.loop = false;

        // Long ambient clips stop immediately; short effects finish their current iteration naturally.
        if (previewChannel == Channel.Ambiance)
            previewSource.Stop();
    }

    public static void Play(Cue cue, Vector3 position = default)
    {
        if (Instance)
            Instance.PlaySound(cue, position);
    }

    void PlaySound(Cue cue, Vector3 position)
    {
        // Missing optional clips are silent, not errors that could interrupt gameplay.
        AudioClip clip = cue == Cue.Ambiance ? (ambiance.Length > 0 ? ambiance[Random.Range(0, ambiance.Length)] : null)
                                             : ((int)cue < clips.Length ? clips[(int)cue] : null);
        if (!clip)
            return;

        // Prefer an idle voice; if the pool is full, replace the oldest sound deterministically.
        Voice voice = voices[0];
        foreach (var candidate in voices)
        {
            if (!candidate.source.isPlaying && !candidate.paused)
            {
                voice = candidate;
                break;
            }
            if (candidate.started < voice.started)
                voice = candidate;
        }
        voice.source.Stop();
        voice.paused = false;
        voice.cue = cue;
        voice.started = Time.unscaledTime;
        voice.world =
            cue != Cue.BrightonDead && cue != Cue.ButtonHover && cue != Cue.ButtonPress && cue != Cue.ButtonRelease;
        voice.source.clip = clip;
        voice.source.loop = false;
        ApplyGain(voice);
        voice.source.transform.position = position;

        // Only enemy steps are spatial; other requested effects stay clear and centered.
        bool spatial = cue == Cue.DarktonStep;
        voice.source.spatialBlend = spatial ? 1 : 0;
        voice.source.spread = 0;
        voice.source.rolloffMode = AudioRolloffMode.Linear;
        voice.source.minDistance = 0.25f;
        voice.source.maxDistance = enemyHearingDistance;
        voice.reverb.enabled = spatial;

        // Hits, pickups, placement, gunshots, and steps get small independent pitch variations.
        bool varied = cue == Cue.BrightonHit || cue == Cue.DarktonHit || cue == Cue.ItemPickup || cue == Cue.Place ||
                      cue == Cue.Shoot || cue == Cue.BrightonStep || cue == Cue.DarktonStep;
        voice.source.pitch = varied ? Random.Range(pitchVariation.x, pitchVariation.y) : 1;
        UpdateEnemyAcoustics();
        voice.source.Play();
    }

    void UpdateEnemyAcoustics()
    {
        if (!listener)
            return;

        // Native 3D rolloff lowers volume; a wetter/longer reverb creates distant enemy presence.
        foreach (var voice in voices)
        {
            if (voice.cue != Cue.DarktonStep || !voice.reverb.enabled)
                continue;
            float distance = Vector3.Distance(voice.source.transform.position, listener.transform.position);
            float far = Mathf.Clamp01(distance / Mathf.Max(1, enemyHearingDistance));
            voice.reverb.reverbPreset = AudioReverbPreset.User;
            voice.reverb.dryLevel = Mathf.Lerp(0, -1200, far);
            voice.reverb.room = Mathf.Lerp(-5000, -1000, far);
            voice.reverb.reverbLevel = Mathf.Lerp(-4000, -500, far);
            voice.reverb.decayTime = Mathf.Lerp(0.25f, 1.8f, far);
        }
    }

    public static void BeginButtonPress()
    {
        if (!Instance)
            return;
        Instance.buttonDown = true;
        Play(Cue.ButtonPress);
    }

    public void ResetWorldSounds()
    {
        // A new life must not resume paused enemy footsteps from the previous life.
        ambianceAge = 0;
        foreach (var voice in voices)
            if (voice.world)
            {
                voice.source.Stop();
                voice.paused = false;
            }
    }

    void OnDestroy()
    {
        if (Instance == this)
            Instance = null;
        for (int i = 0; i < previousListeners.Length; i++)
            if (previousListeners[i])
                previousListeners[i].enabled = previousListenerEnabled[i];
    }
}
