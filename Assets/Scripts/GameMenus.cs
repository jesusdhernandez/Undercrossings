using UnityEngine;
using UnityEngine.InputSystem;

// Owns main/pause/death/settings screens and pauses gameplay while any menu blocks input.
// Immediate-mode GUI uses a 1280x720 layout, scaled uniformly to the current screen.
[DefaultExecutionOrder(-100)]
public sealed class GameMenus : MonoBehaviour
{
    // Shared authored artwork/font and the camera's color-assistance material.
    public Font font;
    public Material colorFilterMaterial;
    AccessibilityPass colorPass;
    public Texture2D normal, hovered, pressed, disabled, slider, thumb, candle, checkmark, brightonMenu;
    public static bool Blocking { get; private set; }

    // Menu flags and unscaled timers keep transitions working while Time.timeScale is zero.
    WorldsidesGameplay game;
    bool pause, settings, wasDead, fading;
    public bool MainMenu { get; private set; } = true;
    float dim, fadeStart, fadeAge, volume, peek, strength;
    float deathMenuAge;
    bool pointerHover;
#if !UNITY_EDITOR
    bool pointerSet;
    Texture2D pointerTexture;
#endif
    public float DeathMenuOpacity => Mathf.Clamp01(deathMenuAge);

    // Settings values are loaded/saved through PlayerPrefs; -1 means no active drag/rebind.
    int filter, resolution;
    bool fullscreen;
    Resolution[] resolutions;
    GUIStyle label, title, mainTitle, buttonText;
    int dragging = -1;
    bool controls;
    bool audioSettings, buttonHoveredThisPass;
    string hoveredButton;
    Vector2 audioScroll;
    int awaitingKey = -1;
    readonly string[] bindingNames = { "Move up", "Move down", "Move left", "Move right", "Sprint" };
    Key[] keys;
    public float Dim => dim;

    void Awake()
    {
        // Block simulation before any gameplay Start/Update can run.
        Blocking = true;
        dim = 1;
        Time.timeScale = 0;
    }

    // Restore saved settings, prepare display choices, and connect the camera filter.
    void Start()
    {
        game = FindFirstObjectByType<WorldsidesGameplay>();
        var sizes = new System.Collections.Generic.List<Resolution>();

        // Collapse refresh-rate duplicates so the resolution button cycles unique dimensions.
        foreach (var candidate in Screen.resolutions)
        {
            int existing = sizes.FindIndex(r => r.width == candidate.width && r.height == candidate.height);
            if (existing >= 0)
                sizes[existing] = candidate;
            else
                sizes.Add(candidate);
        }

        // Load continuous settings with sensible first-run defaults.
        resolutions = sizes.ToArray();
        volume = PlayerPrefs.GetFloat("Settings.Volume", 0.5f);
        peek = PlayerPrefs.GetFloat("Settings.Peek", 1);
        filter = PlayerPrefs.GetInt("Settings.Filter", 0);
        strength = PlayerPrefs.GetFloat("Settings.FilterStrength", 1);
        fullscreen = Screen.fullScreen;
        resolution = Mathf.Max(0, resolutions.Length - 1);

        // Match the current display, then override it only when a saved size exists.
        for (int i = 0; i < resolutions.Length; i++)
            if (resolutions[i].width == Screen.width && resolutions[i].height == Screen.height)
                resolution = i;

        // Restore saved display mode; search the deduplicated list for its selected index.
        if (PlayerPrefs.HasKey("Settings.Width"))
        {
            int w = PlayerPrefs.GetInt("Settings.Width"), h = PlayerPrefs.GetInt("Settings.Height");
            fullscreen = PlayerPrefs.GetInt("Settings.Fullscreen", 1) == 1;
            Screen.SetResolution(w, h, fullscreen);
            for (int i = 0; i < resolutions.Length; i++)
                if (resolutions[i].width == w && resolutions[i].height == h)
                    resolution = i;
        }

        // Reuse one render pass and enqueue it for the main camera before each render.
        if (colorFilterMaterial)
            colorPass = new AccessibilityPass(colorFilterMaterial);
        UnityEngine.Rendering.RenderPipelineManager.beginCameraRendering += BeforeCamera;

        // Point sampling keeps the tiny menu artwork crisp when enlarged.
        foreach (var texture in new[] { normal, hovered, pressed, disabled, slider, thumb, candle, checkmark,
                                        brightonMenu })
            if (texture)
                texture.filterMode = FilterMode.Point;

        // All gameplay text shares this font, including the inventory and damage numbers.
        if (font && game)
            game.defaultFont = font;
        keys = new[] { game.moveUpKey, game.moveDownKey, game.moveLeftKey, game.moveRightKey, game.sprintKey };

        // Restore each movement binding independently, keeping defaults for new entries.
        for (int i = 0; i < keys.Length; i++)
            keys[i] = (Key)PlayerPrefs.GetInt("Settings.Key" + i, (int)keys[i]);
        game.attackMouseButton = PlayerPrefs.GetInt("Settings.AttackButton", 0);
        game.interactMouseButton = PlayerPrefs.GetInt("Settings.InteractButton", 1);
        ApplyKeys();
        ApplyContinuous();
    }

    // Handle key capture before ordinary menu navigation, then advance unscaled fades.
    void Update()
    {
        // Menus cannot change gameplay state until the scene controller exists.
        if (!game)
            return;

        // While rebinding, consume the first newly pressed key; Escape cancels the operation.
        if (awaitingKey >= 0 && Keyboard.current != null)
            foreach (var key in Keyboard.current.allKeys)
                if (key.wasPressedThisFrame)
                {
                    // Swap conflicting bindings instead of assigning two actions to the same key.
                    if (key.keyCode != Key.Escape)
                    {
                        for (int i = 0; i < keys.Length; i++)
                            if (i != awaitingKey && keys[i] == key.keyCode)
                                keys[i] = keys[awaitingKey];
                        keys[awaitingKey] = key.keyCode;
                        ApplyKeys();
                    }
                    awaitingKey = -1;
                    return;
                }

        // Escape closes Settings or toggles pause, but cannot dismiss main/death menus or a fade.
        if (Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame && !fading)
            HandleEscape();
        AdvanceTransitions(Time.unscaledDeltaTime);
    }

    // One Escape action closes only the topmost screen; it never both closes skills and opens pause.
    void HandleEscape()
    {
        if (GameAudio.Instance)
            GameAudio.Instance.EndPreview();
        if (game.Skills && game.Skills.IsOpen)
            game.Skills.Close();
        else if (settings && audioSettings)
        {
            Save();
            audioSettings = false;
        }
        else if (settings && controls)
            controls = false;
        else if (settings)
            settings = false;
        else if (!MainMenu && !game.Dead)
            pause = !pause;
    }

    // Use real elapsed seconds so menu fades continue while the gameplay clock is stopped.
    public void AdvanceTransitions(float seconds)
    {
        if (!game)
            return;

        // Initialize death-only fades once; returning from Settings must not restart them.
        if (!MainMenu && game.Dead && !wasDead)
        {
            dim = 0;
            deathMenuAge = 0;
            pause = settings = false;
        }
        wasDead = game.Dead;
        if (!MainMenu && game.Dead)
            deathMenuAge += seconds;
        if (!MainMenu && game.Dead && !fading)
            dim = Mathf.MoveTowards(dim, 1, seconds / 5);

        // Play/Respawn fades from the current black opacity, including an interrupted death dim.
        if (fading)
        {
            fadeAge += seconds;
            dim = Mathf.Lerp(fadeStart, 0, Mathf.Clamp01(fadeAge));
            if (fadeAge >= 1)
                fading = false;
        }

        // Every blocking state freezes scaled simulation; only completed fades release it.
        Blocking = MainMenu || pause || settings || game.Dead || fading || (game.Skills && game.Skills.IsOpen);
        Time.timeScale = Blocking ? 0 : 1;
    }

    // Reset the current life immediately, but keep gameplay blocked during the one-second reveal.
    public void BeginRespawn()
    {
        MainMenu = false;
        wasDead = false;
        fadeStart = dim;
        fadeAge = 0;
        fading = true;
        pause = settings = false;
        game.Respawn();
        Time.timeScale = 0;
        Blocking = true;
    }

    // Main-menu Play uses the same reset/reveal path as Respawn.
    public void BeginPlay() => BeginRespawn();

    // Discard nested menu interactions and cover the paused world with opaque black.
    public void ReturnToMainMenu()
    {
        if (game.Skills)
            game.Skills.Close();
        MainMenu = true;
        pause = settings = controls = fading = false;
        audioSettings = false;
        awaitingKey = dragging = -1;
        dim = 1;
        Blocking = true;
        Time.timeScale = 0;
    }

    // Preview sliders immediately; saving to disk is handled separately by Save.
    void ApplyContinuous()
    {
        // Use the same logarithmic, unity-at-50% curve for master and category sliders.
        GameAudio.SetMasterVolume(volume);

        // Peek is a multiplier of the game's baseline camera offset.
        if (game)
            game.peekDistance = 0.6f * peek;
        Shader.SetGlobalInt("_AccessibilityMode", filter);
        Shader.SetGlobalFloat("_AccessibilityStrength", strength);

        // Use explicit per-material values as well as globals: a shared blit
        // material must not retain a previous mode in a cached render pass.
        if (colorFilterMaterial)
        {
            colorFilterMaterial.SetInt("_AccessibilityMode", filter);
            colorFilterMaterial.SetFloat("_AccessibilityStrength", strength);
        }
    }

    // Persist input, accessibility, volume, and display choices for the next launch.
    void Save()
    {
        if (GameAudio.Instance)
            GameAudio.Instance.SaveVolumes();
        // Each binding uses its own key so future defaults do not erase user changes.
        for (int i = 0; i < keys.Length; i++)
            PlayerPrefs.SetInt("Settings.Key" + i, (int)keys[i]);
        PlayerPrefs.SetInt("Settings.AttackButton", game.attackMouseButton);
        PlayerPrefs.SetInt("Settings.InteractButton", game.interactMouseButton);
        PlayerPrefs.SetFloat("Settings.Volume", volume);
        PlayerPrefs.SetFloat("Settings.Peek", peek);
        PlayerPrefs.SetInt("Settings.Filter", filter);
        PlayerPrefs.SetFloat("Settings.FilterStrength", strength);
        PlayerPrefs.SetInt("Settings.Fullscreen", fullscreen ? 1 : 0);

        // Some environments expose no resolution list; fullscreen can still be applied there.
        if (resolutions.Length > 0)
        {
            var r = resolutions[resolution];
            Screen.SetResolution(r.width, r.height, fullscreen);
            PlayerPrefs.SetInt("Settings.Width", r.width);
            PlayerPrefs.SetInt("Settings.Height", r.height);
        }
        else
            Screen.fullScreen = fullscreen;
        PlayerPrefs.Save();
    }

    // Draw the active menu over a black/dim layer without changing other GUI systems' transforms.
    void OnGUI()
    {
        if (!game)
            return;

        // Create styles once; every visual state stays white so ordinary labels never dim on hover.
        if (label == null)
        {
            label = new GUIStyle(GUI.skin.label) { font = font, fontSize = 26, alignment = TextAnchor.MiddleCenter,
                                                   normal = { textColor = Color.white } };
            title = new GUIStyle(label) { fontSize = 58 };
            mainTitle = new GUIStyle(title) { fontSize = 116 };
            buttonText = new GUIStyle(label) { fontSize = 32 };

            // Clear skin backgrounds and hover colors inherited from Unity's default styles.
            foreach (var style in new[] { label, title, mainTitle, buttonText })
                foreach (var state in new[] { style.normal, style.hover, style.active, style.focused, style.onNormal,
                                              style.onHover, style.onActive, style.onFocused })
                {
                    state.textColor = Color.white;
                    state.background = null;
                }
        }

        // Scale the reference layout and center it with letterboxing on other aspect ratios.
        pointerHover = false;
        buttonHoveredThisPass = false;
        GUI.depth = -100;
        float scale = Mathf.Min(Screen.width / 1280f, Screen.height / 720f);
        var previous = GUI.matrix;
        GUI.matrix = Matrix4x4.TRS(new Vector3((Screen.width - 1280 * scale) / 2, (Screen.height - 720 * scale) / 2),
                                   Quaternion.identity, Vector3.one * scale);

        // Main menu is opaque; pause/settings dim the scene; death/respawn use animated opacity.
        bool skills = game.Skills && game.Skills.IsOpen;
        if (dim > 0 || MainMenu || pause || settings || skills)
        {
            GUI.color = new Color(0, 0, 0,
                                  skills                              ? 0.94f
                                  : MainMenu                          ? 1
                                  : !game.Dead && (pause || settings) ? 0.8f
                                                                      : dim);
            GUI.DrawTexture(new Rect(-Screen.width / scale, -Screen.height / scale, Screen.width * 3 / scale,
                                     Screen.height * 3 / scale),
                            Texture2D.whiteTexture);
            GUI.color = Color.white;
        }

        // Do not draw clickable menu controls during the transition back into gameplay.
        if (!fading)
        {
            if (skills)
                game.Skills.Draw(this);
            else if (settings)
                DrawSettings();
            else if (MainMenu)
                DrawMainMenu();
            else if (game.Dead)
            {
                GUI.color = new Color(1, 1, 1, DeathMenuOpacity);
                DrawDeath();
                GUI.color = Color.white;
            }
            else if (pause)
                DrawPause();
        }

        // Restore the previous matrix before updating the platform-specific pointer cursor.
        GUI.matrix = previous;
        if (!buttonHoveredThisPass && Event.current.type == EventType.Repaint)
            hoveredButton = null;
        UpdatePointer();
    }

    // Death and main menu share column placement but use different text and artwork.
    void DrawDeath()
    {
        DrawTwoColumnMenu(false);
    }

    void DrawMainMenu() => DrawTwoColumnMenu(true);

    // Keep the button stack centered in the left column and fit artwork into the right.
    void DrawTwoColumnMenu(bool main)
    {
        GUI.Label(new Rect(40, main ? 5 : 30, 1200, main ? 150 : 100),
                  main ? "Undercrossings" : "Brighton's light went out.", main ? mainTitle : title);

        // Content below title: two equal columns; button stack centered in the left.
        float x = 160, y = 309;

        // Play and Respawn both reveal a reset life; main-menu Quit is the only exit button.
        if (Button(new Rect(x, y, 320, 64), main ? "Play" : "Respawn"))
        {
            if (main)
                BeginPlay();
            else
                BeginRespawn();
        }

        // Settings preserves the underlying menu flags, so Back returns to the right screen.
        if (Button(new Rect(x, y + 84, 320, 64), "Settings"))
            settings = true;
        if (Button(new Rect(x, y + 168, 320, 64), main ? "Quit" : "Return to Main Menu"))
        {
            if (main)
                Quit();
            else
                ReturnToMainMenu();
        }

        // Preserve artwork proportions rather than stretching it to fill the column.
        var artwork = main ? brightonMenu : candle;
        if (artwork)
            GUI.DrawTexture(new Rect(780, 165, 360, 500), artwork, ScaleMode.ScaleToFit);
    }

    // Pause keeps the current life intact; returning to main menu does not quit the application.
    void DrawPause()
    {
        GUI.Label(new Rect(40, 60, 1200, 90), "Paused", title);
        if (Button(new Rect(480, 240, 320, 64), "Resume"))
            pause = false;
        if (Button(new Rect(480, 324, 320, 64), "Settings"))
            settings = true;
        if (Button(new Rect(480, 408, 320, 64), "Return to Main Menu"))
            ReturnToMainMenu();
    }

    // Draw adjustable settings, applying previews live and persisting them on either exit button.
    void DrawSettings()
    {
        GUI.Label(new Rect(40, 10, 1200, 80), "Settings", title);
        if (audioSettings)
        {
            DrawAudioSettings();
            return;
        }

        // The binding editor replaces this page while keeping Settings as its parent menu.
        if (controls)
        {
            DrawControls();
            return;
        }
        if (Button(new Rect(1050, 25, 190, 50), "Controls"))
            controls = true;
        if (Button(new Rect(840, 25, 190, 50), "Audio"))
            audioSettings = true;

        // Continuous settings use custom sliders with percentage labels above the thumb.
        volume = Slider(new Rect(520, 145, 530, 32), "Master volume", volume, 0, true);
        peek = Slider(new Rect(520, 245, 530, 32), "Camera peek", peek, 1);
        GUI.Label(new Rect(120, 300, 360, 50), "Color assistance", label);

        // Cycle the three assistance modes and Off; strength blends the correction with the original.
        string[] modes = { "Off", "Protanopia", "Deuteranopia", "Tritanopia" };
        if (Button(new Rect(520, 300, 530, 50), modes[filter]))
            filter = (filter + 1) % modes.Length;
        strength = Slider(new Rect(520, 420, 530, 32), "Filter strength", strength, 2);

        // Fullscreen uses the button frame as a checkbox and a separate bulb as its checkmark.
        GUI.Label(new Rect(120, 485, 260, 68), "Fullscreen", label);
        if (Button(new Rect(396, 478, 80, 80), ""))
            fullscreen = !fullscreen;
        if (fullscreen && checkmark)
            GUI.DrawTexture(new Rect(408, 484, 56, 68), checkmark, ScaleMode.ScaleToFit);

        // Cycle supported display sizes; no list means show the current dimensions unchanged.
        string display = resolutions.Length > 0 ? resolutions[resolution].width + " x " + resolutions[resolution].height
                                                : Screen.width + " x " + Screen.height;
        if (Button(new Rect(520, 485, 530, 56), display) && resolutions.Length > 0)
            resolution = (resolution + 1) % resolutions.Length;
        if (Button(new Rect(300, 600, 300, 64), "Apply"))
        {
            Save();
            settings = false;
        }
        if (Button(new Rect(680, 600, 300, 64), "Back"))
        {
            Save();
            settings = false;
        }
        ApplyContinuous();
    }

    // A scrollable subpage keeps all sound channels readable at the standard menu scale.
    void DrawAudioSettings()
    {
        var audio = GameAudio.Instance;
        if (!audio)
            return;

        audioScroll = GUI.BeginScrollView(new Rect(40, 100, 1200, 470), audioScroll,
                                          new Rect(0, 0, 1160, (GameAudio.Labels.Length + 1) * 100));

        // Master retains its logarithmic gain, independently multiplying every sound category.
        volume = Slider(new Rect(520, 50, 530, 32), "Master volume", volume, 100, true);
        for (int i = 0; i < GameAudio.Labels.Length; i++)
        {
            var channel = (GameAudio.Channel)i;
            float value = Slider(new Rect(520, 150 + i * 100, 530, 32), GameAudio.Labels[i], audio.GetVolume(channel),
                                 101 + i, true, channel);
            audio.SetVolume(channel, value);
        }

        GUI.EndScrollView();
        if (Button(new Rect(480, 600, 320, 64), "Back"))
        {
            Save();
            audioSettings = false;
        }
        ApplyContinuous();
    }

    // Copy working settings bindings into the fields read by player input each frame.
    void ApplyKeys()
    {
        game.moveUpKey = keys[0];
        game.moveDownKey = keys[1];
        game.moveLeftKey = keys[2];
        game.moveRightKey = keys[3];
        game.sprintKey = keys[4];
    }

    // Rebind movement/sprint and cycle mouse assignments without sharing attack/interact buttons.
    void DrawControls()
    {
        // A clicked keyboard row arms the key-capture logic in Update.
        for (int i = 0; i < 5; i++)
        {
            GUI.Label(new Rect(180, 105 + i * 60, 320, 50), bindingNames[i], label);
            if (Button(new Rect(540, 105 + i * 60, 480, 50), awaitingKey == i ? "Press a key..." : keys[i].ToString()))
                awaitingKey = i;
        }

        // Mouse assignments cycle through three buttons; conflicts move the other action forward.
        string[] mouseNames = { "Left mouse", "Right mouse", "Middle mouse" };
        GUI.Label(new Rect(180, 420, 320, 50), "Attack", label);
        if (Button(new Rect(540, 420, 480, 50), mouseNames[game.attackMouseButton]))
        {
            game.attackMouseButton = (game.attackMouseButton + 1) % 3;
            if (game.attackMouseButton == game.interactMouseButton)
                game.interactMouseButton = (game.interactMouseButton + 1) % 3;
        }
        GUI.Label(new Rect(180, 480, 320, 50), "Interact / place", label);
        if (Button(new Rect(540, 480, 480, 50), mouseNames[game.interactMouseButton]))
        {
            game.interactMouseButton = (game.interactMouseButton + 1) % 3;
            if (game.interactMouseButton == game.attackMouseButton)
                game.attackMouseButton = (game.attackMouseButton + 1) % 3;
        }
        if (Button(new Rect(480, 600, 320, 64), "Back"))
        {
            awaitingKey = -1;
            controls = false;
            Save();
        }
    }

    // Draw a three-piece track; mouse drag maps the visible thumb's travel to a 0..1 value.
    float Slider(Rect rect, string name, float value, int id, bool previewAudio = false,
                 GameAudio.Channel? previewChannel = null)
    {
        HoverPointer(rect);
        GUI.Label(new Rect(120, rect.y - 10, 360, 50), name, label);

        // Thumb PNG is 16x8, but its opaque bulb is 6x6 at (5,1).
        // Use the visible bulb bounds so it touches the caps' inner edges.
        const float pixel = 4, opaqueHalfWidth = 12;
        float start = rect.x + 5 * pixel + opaqueHalfWidth, end = rect.xMax - 5 * pixel - opaqueHalfWidth;

        // Keep fixed-width end caps and stretch only the six-column center strip.
        if (slider)
        {
            TexturePart(new Rect(rect.x, rect.y, 20, 32), slider, new Rect(0, 0, 5, 8));
            TexturePart(new Rect(rect.x + 20, rect.y, rect.width - 40, 32), slider, new Rect(5, 0, 6, 8));
            TexturePart(new Rect(rect.xMax - 20, rect.y, 20, 32), slider, new Rect(11, 0, 5, 8));
        }

        // Capture clicks/drags only for this slider; releasing anywhere ends the drag.
        var e = Event.current;
        if (e.type == EventType.MouseDown && e.button == 0 && rect.Contains(e.mousePosition))
        {
            dragging = id;
            value = Mathf.InverseLerp(start, end, e.mousePosition.x);
            if (previewAudio && GameAudio.Instance)
                GameAudio.Instance.BeginPreview(previewChannel);
            e.Use();
        }
        if (dragging == id && (e.type == EventType.MouseDrag || e.type == EventType.MouseDown))
        {
            value = Mathf.InverseLerp(start, end, e.mousePosition.x);
            e.Use();
        }
        if (e.rawType == EventType.MouseUp && e.button == 0 && dragging == id)
        {
            if (previewAudio && GameAudio.Instance)
                GameAudio.Instance.EndPreview();
            dragging = -1;
        }

        // Place the bulb thumb and its percentage using the same normalized value.
        float x = Mathf.Lerp(start, end, value);
        if (thumb)
            GUI.DrawTexture(new Rect(x - 32, rect.y, 64, 32), thumb);
        GUI.Label(new Rect(x - 60, rect.y - 40, 120, 32), Mathf.RoundToInt(value * 100) + "%", label);
        return value;
    }

    // Draw custom button artwork/text, while Unity handles click activation with an invisible button.
    bool Button(Rect rect, string text, Texture2D fixedTexture = null, string soundId = null)
    {
        HoverPointer(rect);
        Color tint = GUI.color;
        bool hover = rect.Contains(Event.current.mousePosition);

        // GUI draws several passes per frame; only play hover when entering a different button.
        if (GUI.enabled && hover)
        {
            buttonHoveredThisPass = true;
            string key =
                soundId ?? rect.ToString() + MainMenu + settings + controls + audioSettings + game.Dead + pause;
            if (hoveredButton != key)
            {
                hoveredButton = key;
                GameAudio.Play(GameAudio.Cue.ButtonHover);
            }

            // Play one press sound before GUI.Button consumes the mouse-down event; holding never repeats it.
            if (Event.current.type == EventType.MouseDown && Event.current.button == 0)
                GameAudio.BeginButtonPress();
        }

        // Select the appropriate texture without changing label colors on hover.
        Texture2D texture = !GUI.enabled                                                           ? disabled
                            : hover && Mouse.current != null && Mouse.current.leftButton.isPressed ? pressed
                            : hover                                                                ? hovered
                                                                                                   : normal;
        // Skill-node state controls its texture; hover scaling must not alter its available/unlocked appearance.
        if (fixedTexture)
            texture = GUI.enabled && hover && Mouse.current != null && Mouse.current.leftButton.isPressed
                          ? pressed
                          : fixedTexture;
        NineSlice(rect, texture ? texture : normal);
        bool clicked = GUI.Button(rect, GUIContent.none, GUIStyle.none);

        // Offset a black copy to give the label a pixel-style shadow, respecting menu fade alpha.
        GUI.color = new Color(0, 0, 0, tint.a * 0.85f);
        GUI.Label(new Rect(rect.x + 2, rect.y + 2, rect.width, rect.height), text, buttonText);
        GUI.color = tint;
        GUI.Label(rect, text, buttonText);
        return clicked;
    }

    public bool OpenSkills()
    {
        if (!game || !game.Skills || Blocking || game.Dead)
            return false;
        game.Skills.Open();
        AdvanceTransitions(0);
        return true;
    }

    // Reuse tiled frames, cursor hints, and all button sounds without replacing node state on hover.
    public bool SkillButton(Rect rect, Texture2D texture, bool clickable, string soundId)
    {
        bool previous = GUI.enabled;
        GUI.enabled = previous && clickable;
        bool clicked = Button(rect, "", texture, soundId);
        GUI.enabled = previous;
        return clicked;
    }

    // Convert a source pixel rectangle to normalized UV coordinates for partial texture drawing.
    static void TexturePart(Rect dest, Texture2D texture, Rect source)
    {
        GUI.DrawTextureWithTexCoords(dest, texture,
                                     new Rect(source.x / texture.width, source.y / texture.height,
                                              source.width / texture.width, source.height / texture.height));
    }

    // Keep four corners fixed; tile the center and edges instead of stretching their checkerboard.
    void NineSlice(Rect rect, Texture2D texture)
    {
        if (!texture)
            return;

        // One-based (4,3) means zero-based (3,2); borders are 3,3,2,2.
        const float scale = 4, bx = 12, by = 8;

        // Visit the nine source regions and determine each matching destination rectangle.
        for (int y = 0; y < 3; y++)
            for (int x = 0; x < 3; x++)
            {
                Rect source = new Rect(x == 0 ? 0 : x == 1 ? 3 : 5, y == 0 ? 0 : y == 1 ? 2 : 4, x == 1 ? 2 : 3, 2);
                Rect dest = new Rect(x == 0   ? rect.x
                                     : x == 1 ? rect.x + bx
                                              : rect.xMax - bx,
                                     y == 0   ? rect.yMax - by
                                     : y == 1 ? rect.y + by
                                              : rect.y,
                                     x == 1 ? rect.width - 2 * bx : bx, y == 1 ? rect.height - 2 * by : by);

                // Corners are drawn once; any middle row/column repeats in native-size tiles.
                if (x != 1 && y != 1)
                {
                    TexturePart(dest, texture, source);
                    continue;
                }
                float tileWidth = source.width * scale, tileHeight = source.height * scale;

                // Crop the final tile in each row/column so the pattern ends exactly at the border.
                for (float yy = 0; yy < dest.height; yy += tileHeight)
                    for (float xx = 0; xx < dest.width; xx += tileWidth)
                    {
                        float w = Mathf.Min(tileWidth, dest.width - xx), h = Mathf.Min(tileHeight, dest.height - yy);
                        TexturePart(new Rect(dest.x + xx, dest.y + yy, w, h), texture,
                                    new Rect(source.x, source.y + source.height - h / scale, w / scale, h / scale));
                    }
            }
    }

    // Stop Play Mode in the editor, or close the standalone application in a build.
    void Quit()
    {
        Time.timeScale = 1;
#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#else
        Application.Quit();
#endif
    }

    // Enqueue the shared accessibility pass for the gameplay camera only.
    void BeforeCamera(UnityEngine.Rendering.ScriptableRenderContext context, Camera camera)
    {
        // Keep an identity blit when Off, rather than leaving a previous filtered
        // intermediate target in the final camera path.
        if (colorPass != null && camera == Camera.main)
            UnityEngine.Rendering.Universal.CameraExtensions.GetUniversalAdditionalCameraData(camera)
                .scriptableRenderer.EnqueuePass(colorPass);
    }

    // Detach render callbacks and restore global time/cursor state when the scene is removed.
    void OnDestroy()
    {
        UnityEngine.Rendering.RenderPipelineManager.beginCameraRendering -= BeforeCamera;
        Blocking = false;
        Time.timeScale = 1;
        Cursor.SetCursor(null, Vector2.zero, CursorMode.Auto);
#if !UNITY_EDITOR
        if (pointerTexture)
            Destroy(pointerTexture);
#endif
    }

    // Mark interactive regions; the editor can request its native link cursor directly.
    void HoverPointer(Rect rect)
    {
        // Disabled controls and non-hovered rectangles must not request a hand cursor.
        if (!GUI.enabled || !rect.Contains(Event.current.mousePosition))
            return;
        pointerHover = true;
#if UNITY_EDITOR
        UnityEditor.EditorGUIUtility.AddCursorRect(rect, UnityEditor.MouseCursor.Link);
#endif
    }

    // Standalone builds use a generated hand because Unity exposes no runtime native-link cursor.
    void UpdatePointer()
    {
#if !UNITY_EDITOR
        // Only change the system cursor when entering/leaving an interactive region.
        if (pointerSet == pointerHover)
            return;
        pointerSet = pointerHover;

        // Build the portable cursor once, scaling each pattern pixel to a 2x2 block.
        if (pointerSet && !pointerTexture)
        {
            // Portable link hand for builds; Unity has no runtime system-hand enum.
            string[] hand = { "....##..........", "...#ww#.........", "...#ww#.........", "...#ww#.........",
                              "...#ww###.......", "...#ww#ww###....", "...#wwwwwwww#...", ".###wwwwwwww#...",
                              "#ww#wwwwwwww#...", "#wwwwwwwwwww#...", ".#wwwwwwwwww#...", "..#wwwwwwwww#...",
                              "...#wwwwwww#....", "....#wwwwww#....", "....########....", "................" };
            pointerTexture = new Texture2D(32, 32, TextureFormat.RGBA32, false);
            pointerTexture.filterMode = FilterMode.Point;

            // Pattern rows are top-down; Texture2D pixels count upward from the bottom.
            for (int y = 0; y < 32; y++)
                for (int x = 0; x < 32; x++)
                {
                    char pixel = hand[y / 2][x / 2];
                    pointerTexture.SetPixel(x, 31 - y,
                                            pixel == '#'   ? Color.black
                                            : pixel == 'w' ? Color.white
                                                           : Color.clear);
                }
            pointerTexture.Apply();
        }
        Cursor.SetCursor(pointerSet ? pointerTexture : null, pointerSet ? new Vector2(9, 1) : Vector2.zero,
                         CursorMode.Auto);
#endif
    }
}
