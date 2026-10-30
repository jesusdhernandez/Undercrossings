using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

// Owns one life's random skill grid, purchased upgrades, and the pannable/zoomable menu view.
public sealed class SkillTree : MonoBehaviour
{
    public enum Buff
    {
        MeleeDamage,
        BulletDamage,
        BulletLight,
        MaxHealth,
        Regeneration,
        BulbChance,
        StickChance,
        UnlockFireworks,
        FireworkChance,
        MeleeRange,
        BulletRange
    }

    public sealed class Node
    {
        public Vector2Int cell;
        public Buff buff;
        public int magnitude;
        public bool unlocked;
        public float created;
    }

    static readonly Vector2Int[] Neighbors = { Vector2Int.up, Vector2Int.right, Vector2Int.down, Vector2Int.left };
    readonly Dictionary<Vector2Int, Node> nodes = new Dictionary<Vector2Int, Node>();
    readonly List<Node> drawNodes = new List<Node>();
    public IReadOnlyDictionary<Vector2Int, Node> Nodes => nodes;
    public int Points { get; private set; }
    public int UnlockedCount { get; private set; }
    public bool FireworksUnlocked { get; private set; }
    public float FireworkChance { get; private set; }
    public bool IsOpen { get; private set; }
    public Vector2 Pan { get; private set; }
    public float Zoom { get; private set; } = 1;
    [Min(1)]
    public int pointCost = 3;
    public Texture2D healthIcon, fireworkIcon;

    WorldsidesGameplay game;
    float opened;
    bool dragging;
    bool holdingBuy;
    float buyHoldAge, nextBuyRepeat;
    Rect buyScreenRect;
    GUIStyle text, explanation;
    BaseStats baseline;

    // Keep authored starting values separate from upgrades so death restores the exact initial balance.
    struct BaseStats
    {
        public float unarmed, stick, bullet, health, regen, unarmedRange, stickRange, bulletRange;
        public float lightRange, lightIntensity, stickChance, bulbChance;
    }

    public void Initialize(WorldsidesGameplay owner)
    {
        game = owner;
        baseline = new BaseStats { unarmed = game.unarmedDamage,
                                   stick = game.stickDamage,
                                   bullet = game.gunDamage,
                                   health = game.maximumHealth,
                                   regen = game.regenerationHealth,
                                   unarmedRange = game.unarmedRangePixels,
                                   stickRange = game.stickRangePixels,
                                   bulletRange = game.gunRange,
                                   lightRange = game.bulletLightRange,
                                   lightIntensity = game.bulletLightIntensity,
                                   stickChance = game.stickDropChance,
                                   bulbChance = game.bulbDropChance };
        ResetProgress();
    }

    public void ResetProgress()
    {
        if (!game)
            return;

        // Restore tuned base stats, including drop probabilities that may have exceeded 100%.
        game.unarmedDamage = baseline.unarmed;
        game.stickDamage = baseline.stick;
        game.gunDamage = baseline.bullet;
        game.maximumHealth = baseline.health;
        game.regenerationHealth = baseline.regen;
        game.unarmedRangePixels = baseline.unarmedRange;
        game.stickRangePixels = baseline.stickRange;
        game.gunRange = baseline.bulletRange;
        game.bulletLightRange = baseline.lightRange;
        game.bulletLightIntensity = baseline.lightIntensity;
        game.stickDropChance = baseline.stickChance;
        game.bulbDropChance = baseline.bulbChance;

        Points = UnlockedCount = 0;
        FireworksUnlocked = false;
        FireworkChance = 0;
        IsOpen = dragging = holdingBuy = false;
        Pan = Vector2.zero;
        Zoom = 1;
        nodes.Clear();

        // Initially show only the center and its four locked cardinal neighbors.
        EnsureNode(Vector2Int.zero);
        foreach (var direction in Neighbors)
            EnsureNode(direction);
    }

    public void Open()
    {
        IsOpen = true;
        opened = Time.unscaledTime;
        dragging = false;
    }

    public void Close()
    {
        IsOpen = dragging = holdingBuy = false;
    }

    // Buying uses real time because the skill menu pauses gameplay time.
    void Update()
    {
        if (!IsOpen || !holdingBuy)
            return;
        var mouse = Mouse.current;
        if (mouse == null || !mouse.leftButton.isPressed || !Application.isFocused)
        {
            holdingBuy = false;
            return;
        }
        Vector2 pointer = mouse.position.ReadValue();
        pointer.y = Screen.height - pointer.y;
        AdvanceBuyHold(Time.unscaledDeltaTime, buyScreenRect.Contains(pointer));
    }

    public void BeginBuyHold()
    {
        holdingBuy = true;
        buyHoldAge = 0;
        nextBuyRepeat = 0.5f;
        BuyPoint();
    }

    // Each hold starts with half-second repeats, then gradually accelerates to a safe 20 attempts/sec cap.
    public void AdvanceBuyHold(float seconds, bool overButton = true)
    {
        if (!IsOpen || !holdingBuy)
            return;
        buyHoldAge += Mathf.Max(0, seconds);
        if (!overButton)
        {
            nextBuyRepeat = buyHoldAge + 0.5f;
            return;
        }
        int attempts = 0;
        while (buyHoldAge >= nextBuyRepeat && attempts++ < 8)
        {
            BuyPoint();
            nextBuyRepeat += Mathf.Max(0.05f, 0.5f * Mathf.Exp(-0.12f * (nextBuyRepeat - 0.5f)));
        }
        // A stalled frame must not queue an unlimited burst of purchases on later frames.
        if (attempts > 8)
            nextBuyRepeat = buyHoldAge + 0.05f;
    }

    public bool IsAvailable(Node node)
    {
        if (node.unlocked)
            return false;
        if (node.cell == Vector2Int.zero)
            return true;

        // Any one purchased cardinal neighbor satisfies the prerequisite; diagonal cells do not.
        foreach (var direction in Neighbors)
            if (nodes.TryGetValue(node.cell + direction, out var neighbor) && neighbor.unlocked)
                return true;
        return false;
    }

    // Roll exclusive outcomes: 1% quintuple, 10% triple, 30% double, and 59% single.
    public static int RollMagnitude(float roll) => roll < 0.01f ? 5 : roll < 0.11f ? 3 : roll < 0.41f ? 2 : 1;

    public bool IsBuffEligible(Buff buff) => buff == Buff.FireworkChance
                                                 ? FireworksUnlocked
                                                 : buff != Buff.UnlockFireworks || !FireworksUnlocked;

    void EnsureNode(Vector2Int cell)
    {
        if (nodes.ContainsKey(cell))
            return;

        // Firework chance cannot appear before the ability is unlocked; the unlock itself appears only before then.
        var choices = new List<Buff>();
        foreach (Buff buff in System.Enum.GetValues(typeof(Buff)))
            if (IsBuffEligible(buff))
                choices.Add(buff);
        nodes.Add(cell, new Node { cell = cell, buff = choices[Random.Range(0, choices.Count)],
                                   magnitude = RollMagnitude(Random.value), created = Time.unscaledTime });
    }

    public bool BuyPoint()
    {
        // A point always costs the same configurable pair of resources, as in the menu mockup.
        if (!IsOpen || Points == int.MaxValue || !game.Inventory.SpendRecipe(pointCost))
            return false;
        Points++;
        return true;
    }

    public bool Unlock(Vector2Int cell)
    {
        if (!IsOpen || Points <= 0 || !nodes.TryGetValue(cell, out var node) || !IsAvailable(node))
            return false;
        node.unlocked = true;
        Points--;
        UnlockedCount++;
        Apply(node.buff, node.magnitude);

        // Reveal immediate neighbors, then grow a locked preview frontier around every available node.
        foreach (var direction in Neighbors)
            EnsureNode(cell + direction);
        var available = new List<Node>();
        foreach (var candidate in nodes.Values)
            if (IsAvailable(candidate))
                available.Add(candidate);
        foreach (var candidate in available)
            foreach (var direction in Neighbors)
                EnsureNode(candidate.cell + direction);
        return true;
    }

    void Apply(Buff buff, int amount)
    {
        // Percent chance upgrades are percentage points; range upgrades are five sprite pixels each.
        switch (buff)
        {
        case Buff.MeleeDamage:
            game.unarmedDamage += amount;
            game.stickDamage += amount;
            break;
        case Buff.BulletDamage:
            game.gunDamage += amount;
            break;
        case Buff.BulletLight:
            float factor = 1 + 0.1f * amount;
            game.bulletLightRange *= Mathf.Sqrt(factor); // Radius grows by sqrt(area multiplier).
            game.bulletLightIntensity *= factor;
            break;
        case Buff.MaxHealth:
            game.AddHealthCapacity(amount);
            break;
        case Buff.Regeneration:
            game.regenerationHealth += amount;
            break;
        case Buff.BulbChance:
            game.bulbDropChance += 0.1f * amount;
            break;
        case Buff.StickChance:
            game.stickDropChance += 0.1f * amount;
            break;
        case Buff.MeleeRange:
            game.unarmedRangePixels += 5 * amount;
            game.stickRangePixels += 5 * amount;
            break;
        case Buff.BulletRange:
            game.gunRange += 5 * amount * game.SpritePixelSize;
            break;
        case Buff.FireworkChance:
            FireworkChance = Mathf.Min(1, FireworkChance + 0.01f * amount);
            break;
        case Buff.UnlockFireworks:
            FireworksUnlocked = true;
            FireworkChance = Mathf.Min(1, 0.01f * amount);

            // Replace unpurchased duplicate unlocks so they remain useful after gaining the ability.
            foreach (var node in nodes.Values)
                if (!node.unlocked && node.buff == Buff.UnlockFireworks)
                    node.buff = Buff.FireworkChance;
            break;
        }
    }

    public static string Description(Node node)
    {
        int n = node.magnitude;
        switch (node.buff)
        {
        case Buff.MeleeDamage:
            return "+" + n + " Melee Damage";
        case Buff.BulletDamage:
            return "+" + n + " Bullet Damage";
        case Buff.BulletLight:
            return "+" + (10 * n) + "% Bullet Light Area";
        case Buff.MaxHealth:
            return "+" + n + " Max HP";
        case Buff.Regeneration:
            return "+" + n + " HP per Regen Tick";
        case Buff.BulbChance:
            return "+" + (10 * n) + "% Bulb Drop Chance";
        case Buff.StickChance:
            return "+" + (10 * n) + "% Stick Drop Chance";
        case Buff.UnlockFireworks:
            return "Unlock Fireworks (" + n + "%)";
        case Buff.FireworkChance:
            return "+" + n + "% Firework Chance";
        case Buff.MeleeRange:
            return "+" + (5 * n) + " Voxels Melee Range";
        default:
            return "+" + (5 * n) + " Voxels Bullet Range";
        }
    }

    Texture2D Icon(Node node, GameMenus menus)
    {
        // Reuse authored pixel art; the hover description distinguishes upgrades with similar icons.
        switch (node.buff)
        {
        case Buff.MaxHealth:
        case Buff.Regeneration:
            return healthIcon;
        case Buff.MeleeDamage:
        case Buff.MeleeRange:
        case Buff.StickChance:
            return game.stickIcon;
        case Buff.BulbChance:
            return menus.checkmark;
        case Buff.BulletDamage:
        case Buff.BulletRange:
            return game.gunIcon;
        case Buff.BulletLight:
            return game.smallLightIcon;
        default:
            return fireworkIcon;
        }
    }

    public void Draw(GameMenus menus)
    {
        if (text == null)
        {
            text = new GUIStyle { font = game.defaultFont, fontSize = 32, alignment = TextAnchor.MiddleCenter };
            text.normal.textColor = Color.white;
            explanation = new GUIStyle(text) { fontSize = 26 };

            // Labels are informational: hovering them must not inherit a dark interactive text color.
            foreach (var style in new[] { text, explanation })
                foreach (var state in new[] { style.normal, style.hover, style.active, style.focused, style.onNormal,
                                              style.onHover, style.onActive, style.onFocused })
                    state.textColor = Color.white;
        }

        // The view uses the same 1280x720 reference coordinates as the existing menus.
        Rect viewport = new Rect(0, 0, 1280, 620);
        Vector2 center = new Vector2(640, 360);
        float opening = 1 - Mathf.Pow(1 - Mathf.Clamp01((Time.unscaledTime - opened) / 0.18f), 3);
        Event e = Event.current;
        if (viewport.Contains(e.mousePosition))
        {
            // Zoom about the cursor so its pointed-to location does not jump sideways.
            if (e.type == EventType.ScrollWheel)
            {
                float zoom = Mathf.Clamp(Zoom * Mathf.Pow(1.12f, -e.delta.y), 0.35f, 2.5f);
                Pan = e.mousePosition - center - (e.mousePosition - center - Pan) * (zoom / Zoom);
                Zoom = zoom;
                e.Use();
            }
            if (e.type == EventType.MouseDown && e.button == 1)
            {
                dragging = true;
                e.Use();
            }
        }
        if (dragging && e.type == EventType.MouseDrag && e.button == 1)
        {
            Pan += e.delta;
            e.Use();
        }
        if (e.rawType == EventType.MouseUp && e.button == 1)
            dragging = false;

        // Cull off-screen cells. New cells and every menu opening float outward over 0.18 real seconds.
        Node hoveredNode = null;
        Rect hoveredRect = default;
        drawNodes.Clear();
        drawNodes.AddRange(nodes.Values);
        GUI.BeginGroup(viewport);
        foreach (var node in drawNodes)
        {
            float age = Mathf.Min(Time.unscaledTime - opened, Time.unscaledTime - node.created);
            float t = 1 - Mathf.Pow(1 - Mathf.Clamp01(age / 0.18f), 3);
            Vector2 target = center + Pan + new Vector2(node.cell.x, -node.cell.y) * (160 * Zoom);
            Vector2 position = Vector2.Lerp(center, target, t);
            float size = Mathf.Max(26, 96 * Zoom * Mathf.Lerp(0.2f, 1, t));
            Rect rect = new Rect(position.x - size / 2, position.y - size / 2, size, size);
            if (!viewport.Overlaps(rect))
                continue;

            bool hover = rect.Contains(e.mousePosition);
            if (hover)
            {
                rect = new Rect(position.x - size * 0.55f, position.y - size * 0.55f, size * 1.1f, size * 1.1f);
                hoveredNode = node;
                hoveredRect = rect;
            }
            bool available = IsAvailable(node);
            Texture2D texture = node.unlocked ? menus.hovered : available ? menus.normal : menus.disabled;
            if (menus.SkillButton(rect, texture, available && t >= 0.99f, "Skill:" + node.cell))
                Unlock(node.cell);

            // Artwork is separate from the button frame and grows with the hover scale.
            Texture2D icon = Icon(node, menus);
            if (icon)
                GUI.DrawTexture(new Rect(rect.x + rect.width * 0.18f, rect.y + rect.height * 0.18f, rect.width * 0.64f,
                                         rect.height * 0.64f),
                                icon, ScaleMode.ScaleToFit);
            if (node.magnitude > 1)
                GUI.Label(new Rect(rect.xMax - 38, rect.y + 4, 34, 28), "x" + node.magnitude, explanation);
        }

        // Tooltips are drawn last so nearby nodes never cover their explanation.
        if (hoveredNode != null)
        {
            string status = hoveredNode.unlocked       ? "Unlocked"
                            : IsAvailable(hoveredNode) ? (Points > 0 ? "1 skill point" : "Need 1 skill point")
                                                       : "Unlock an adjacent node first";
            GUI.Label(new Rect(hoveredRect.center.x - 170, hoveredRect.yMax + 5, 340, 64),
                      Description(hoveredNode) + "\n" + status, explanation);
        }
        GUI.EndGroup();

        // Footer controls also float out from center, then remain fixed while the grid pans/zooms.
        GUI.color = Color.black;
        GUI.DrawTexture(new Rect(0, 620, 1280, 100), Texture2D.whiteTexture);
        GUI.color = Color.white;
        Vector2 pointCenter = Vector2.Lerp(center, new Vector2(205, 670), opening);
        GUI.Label(new Rect(pointCenter.x - 190, pointCenter.y - 30, 380, 60), "You have " + Points + " skill points",
                  explanation);
        GUI.Label(new Rect(20, Mathf.Lerp(340, 6, opening), 1240, 40),
                  "Scroll to zoom  |  Right-drag to pan  |  Esc to close", explanation);
        bool canBuy = game.Inventory.CanCraft(pointCost);
        Vector2 buyCenter = Vector2.Lerp(center, new Vector2(640, 670), opening);
        Rect buyRect = new Rect(buyCenter.x - 235, buyCenter.y - 32, 470, 64);

        // Capture before GUI.Button consumes the press; ignore its release activation to avoid buying twice.
        Vector3 screenMin = GUI.matrix.MultiplyPoint3x4(new Vector3(buyRect.x, buyRect.y, 0));
        Vector3 screenMax = GUI.matrix.MultiplyPoint3x4(new Vector3(buyRect.xMax, buyRect.yMax, 0));
        buyScreenRect = Rect.MinMaxRect(screenMin.x, screenMin.y, screenMax.x, screenMax.y);
        if (e.type == EventType.MouseDown && e.button == 0 && buyRect.Contains(e.mousePosition) && opening >= 0.99f)
            BeginBuyHold();
        menus.SkillButton(buyRect, canBuy ? menus.normal : menus.disabled, opening >= 0.99f, "BuySkillPoint");

        // Measure the text instead of spacing it into oversized columns; each cost sits next to its icon.
        string caption = "Buy Skill Point: ";
        float captionWidth = explanation.CalcSize(new GUIContent(caption)).x;
        float costWidth = explanation.CalcSize(new GUIContent(pointCost.ToString())).x;
        float x = buyCenter.x - (captionWidth + 2 * costWidth + 88) / 2;
        GUI.Label(new Rect(x, buyRect.y + 7, captionWidth, 50), caption, explanation);
        x += captionWidth;
        foreach (var icon in new[] { game.stickIcon, menus.checkmark })
        {
            GUI.Label(new Rect(x, buyRect.y + 7, costWidth, 50), pointCost.ToString(), explanation);
            if (icon)
                GUI.DrawTexture(new Rect(x + costWidth + 4, buyRect.y + 12, 32, 40), icon, ScaleMode.ScaleToFit);
            x += costWidth + 44;
        }

        // Resource totals remain visible in the footer even while the normal gameplay HUD is hidden.
        string[] items = { "Stick", "Bulb" };
        Texture2D[] icons = { game.stickIcon, menus.checkmark };
        for (int i = 0; i < items.Length; i++)
        {
            Vector2 position = Vector2.Lerp(center, new Vector2(1100 + i * 105, 665), opening);
            if (icons[i])
                GUI.DrawTexture(new Rect(position.x - 24, position.y - 36, 48, 48), icons[i], ScaleMode.ScaleToFit);
            GUI.Label(new Rect(position.x - 50, position.y + 15, 100, 32), game.Inventory.Count(items[i]).ToString(),
                      explanation);
        }
    }
}
