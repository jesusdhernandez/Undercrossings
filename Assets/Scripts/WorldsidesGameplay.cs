using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

// Coordinates player input, combat, spawning, loot, camera motion, and gameplay feedback.
// Inspector fields are tuning knobs; runtime state below tracks the current life.
public sealed class WorldsidesGameplay : MonoBehaviour
{
    // References to authored prefabs, sprite sheets, and the shared UI font.
    [Header("Assets")]
    public GameObject brightonPrefab, darktonPrefab, stickBulbPrefab, tallLightPrefab, stickLootPrefab, bulbLootPrefab;
    public GameObject gunLootPrefab;
    public Texture2D healthIcon;
    public Texture2D fireworkIcon;
    public Texture2D brightonWalk, armsWalk, stickWalk, gunWalk, darktonWalk;
    public Texture2D stickIcon, gunIcon, smallLightIcon, tallLightIcon;
    public Font defaultFont;

    // GameMenus loads saved input bindings into these defaults.
    public Key moveUpKey = Key.W, moveDownKey = Key.S, moveLeftKey = Key.A, moveRightKey = Key.D,
               sprintKey = Key.LeftCtrl;
    public int attackMouseButton, interactMouseButton = 1;

    // World movement and sprite-frame timing are separate so facing can differ from motion.
    [Header("Movement (in meters)")]
    [Min(0)]
    public float walkSpeed = 1, sprintSpeed = 1.5f;
    [Min(0.02f)]
    public float walkFrameSeconds = 0.5f, sprintFrameSeconds = 0.25f, enemyFrameSeconds = 0.5f;
    [Min(0.01f)]
    public float playerRadius = 0.12f, enemyRadius = 0.12f;

    // Cursor peek is a temporary camera offset, not player or ground movement.
    [Header("Camera")]
    [Min(0)]
    public float peekDistance = 0.6f;
    [Min(0.01f)]
    public float peekResponse = 6;

    // Combat ranges in pixels are converted using Brighton's sprite scale.
    [Header("Player combat")]
    [Min(0.1f)]
    public float maximumHealth = 5;
    [Min(0)]
    public float regenerationHealth = 1;
    [Min(0.1f)]
    public float regenerationSeconds = 10;
    [Min(0)]
    public float unarmedDamage = 1, stickDamage = 2, gunDamage = 1;
    [Min(0)]
    public float unarmedRangePixels = 5, stickRangePixels = 10;
    [Min(0.05f)]
    public float unarmedCooldown = 0.5f, stickCooldown = 0.7f, gunCooldown = 0.3f;
    [Min(0)]
    public float gunRange = 30f / 32, interactionRange = 10;
    [Tooltip(
        "Maximum light placement distance in Brighton sprite pixels (voxels). Distant clicks clamp to this radius.")]
    [Min(0)]
    public float lightPlacementVoxels = 20;
    [Range(1, 180)]
    public float meleeHalfAngle = 65;

    // Projectile visuals and light settings are copied into each new bullet.
    [Header("Gun bullets")]
    public Material bulletMaterial;
    [Min(0.1f)]
    public float bulletSpeed = 12;
    [Min(0)]
    public float bulletCollisionRadius = 0.025f;
    [Min(0.001f)]
    public float bulletLength = 0.125f, bulletWidth = 0.0625f;
    public float bulletHeight = 0.24f, bulletLightHeight = 0.49f;
    [Min(0)]
    public float bulletLightIntensity = 0.01f;
    [Min(0.01f)]
    public float bulletLightRange = 2;
    public Color bulletLightColor = new Color(1, 0.6060061f, 0, 1);

    // Enemy difficulty rises with distance from the world origin, within safety caps.
    [Header("Enemy progression (at spawn)")]
    [Min(0.1f)]
    public float enemyHealth = 3;
    [Min(0)]
    public float enemySpeedMultiplier = 0.5f, enemyDamage = 1;
    [Min(0.05f)]
    public float enemyCooldown = 3, minimumEnemyCooldown = 0.5f;
    [Tooltip("Combat multiplier = exp(distance * rate). Default doubles every 10m, capped below; loot is separate.")]
    [Min(0)]
    public float exponentialGrowthPerMeter = 0.06931472f;
    [Min(1)]
    public float maximumGrowthMultiplier = 100;

    // Random spawn waits gradually shorten; the living-enemy cap limits crowd size.
    [Header("Spawning")]
    public Vector2 startingSpawnSeconds = new Vector2(5, 10);
    public Vector2 fastestSpawnSeconds = new Vector2(0.5f, 1);
    [Min(0)]
    public float spawnWaitReductionPerMeter = 0.1f;
    [Min(0.5f)]
    public float spawnOffscreenMeters = 1;
    [Min(1)]
    public int maximumLivingEnemies = 100;

    // Pickups use capsule-centered distance; recipes unlock equipment only when affordable.
    [Header("Loot")]
    [Min(0)]
    public float pickupVoxels = 20;
    [Tooltip("Loot quantity growth is independent of combat difficulty; 0.01 restores the original gentle curve.")]
    [Min(0)]
    public float lootGrowthPerMeter = 0.01f;
    public bool stickBulbUnlocked = true, tallLightUnlocked = true;
    [Min(0)]
    public float stickDropChance = 0.5f;
    [Min(0)]
    public float bulbDropChance = 0.5f;
    [Range(0, 1)]
    public float gunDropOnLightChance = 0.1f;
    [Range(0, 1)]
    public float twoItemChance = 0.5f;
    [Min(1)]
    public int maximumDropQuantity = 100;

    // Popup lifetime/font size and the health-based vignette can be tuned independently.
    [Header("Damage feedback")]
    [Min(0.05f)]
    public float damageNumberSeconds = 0.5f;
    [Min(1)]
    public int damageNumberFontSize = 26;
    [Min(0)]
    public float damageNumberRisePixels = 24;
    public Color damageNumberColor = new Color(0.82f, 0.82f, 0.82f, 1);
    [Min(0.05f)]
    public float vignetteFadeSeconds = 1.5f;
    [Range(0, 0.25f)]
    public float heartbeatStrength = 0.06f;
    [Min(0.1f)]
    public float heartbeatDuration = 1.5f;
    [Min(0.01f)]
    public float vignetteCenterRadius = 0.065f;

    // Read-only access lets enemies, loot, and menus share the active player's state.
    public Transform Player { get; private set; }
    public bool Dead { get; private set; }
    public PlayerInventory Inventory { get; private set; }
    public SkillTree Skills { get; private set; }
    public float SpritePixelSize => playerSheet ? Mathf.Abs(playerSheet.PixelSize) : 1f / 32;
    public int SelectedItem => selected;
    public float PickupRadius => pickupVoxels * (playerSheet ? Mathf.Abs(playerSheet.PixelSize) : 1f / 32);

    // Ground-distance checks ignore the returned center's height.
    public Vector3 PickupOrigin
    {
        get {
            // Before player creation there is no capsule to query.
            if (!Player)
                return Vector3.zero;

            // Use the capsule's actual XZ center, independent of the tilted
            // artwork and camera. WorldLoot ignores height for this distance.
            return pickupCapsule ? pickupCapsule.bounds.center : Player.position;
        }
    }
    public readonly List<DarktonAgent> Enemies = new List<DarktonAgent>();
    public System.Action<WorldLoot> InteractedWithLoot;

    // Cached scene objects avoid repeated lookup; timers use scaled time except damage feedback.
    Camera view;
    DirectionalSheet playerSheet;
    MeshRenderer pickupCapsule;
    Vector3 cameraOffset, peek, aim = Vector3.back;
    float health, nextAttack, nextSpawn, lastDamage = -100;
    float regenerationElapsed;
    float displayedMissingHealth;
    int selected;
    Canvas hotbarCanvas;
    InventoryHud inventoryHud;
    Texture2D vignette;
    int vignetteWidth, vignetteHeight;

    // Connect scene/prefab assets and create the systems needed for the first life.
    void Start()
    {
        view = Camera.main;

        // Stop early with a clear error if required authored assets are missing.
        if (!view || !brightonPrefab || !darktonPrefab || !brightonWalk || !darktonWalk)
        {
            Debug.LogError("Worldsides Gameplay is missing its camera or required assets.", this);
            enabled = false;
            return;
        }

        // Reuse an authored Brighton if present, otherwise instantiate the prefab.
        foreach (var root in gameObject.scene.GetRootGameObjects())
            if (root.name == "Brighton")
            {
                Player = root.transform;
                break;
            }
        if (!Player)
            Player = Instantiate(brightonPrefab).transform;
        Player.position = Vector3.zero;

        // The shadow-only capsule supplies the intended ground pickup center.
        foreach (var renderer in Player.GetComponentsInChildren<MeshRenderer>())
            if (renderer.shadowCastingMode == UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly)
            {
                pickupCapsule = renderer;
                break;
            }

        // Body animation creates an equipment card on top of Brighton's visible quad.
        playerSheet = Player.gameObject.AddComponent<DirectionalSheet>();
        playerSheet.Initialize(brightonWalk, true);
        playerSheet.Step += () => GameAudio.Play(GameAudio.Cue.BrightonStep);

        // Keep the authored camera rotation/zoom. Center its ground intersection on the player.
        float distance = Mathf.Max(1, view.transform.position.y) / Mathf.Max(0.05f, -view.transform.forward.y);
        cameraOffset = -view.transform.forward * distance;
        view.transform.position = Player.position + cameraOffset;

        // Bind the recycled ground to Brighton, and initialize any authored enemy instances.
        foreach (var ground in FindObjectsByType<InfiniteGround>(FindObjectsSortMode.None))
            ground.Follow(Player);
        foreach (var root in gameObject.scene.GetRootGameObjects())
            if (root.name == "Darkton" && !root.GetComponent<DarktonAgent>())
                root.AddComponent<DarktonAgent>().Initialize(this, 0);

        // Start with full health and an empty inventory, then build its event-driven HUD.
        health = maximumHealth;
        Inventory = gameObject.AddComponent<PlayerInventory>();
        Inventory.Changed += InventoryChanged;
        Skills = gameObject.AddComponent<SkillTree>();
        Skills.healthIcon = healthIcon;
        Skills.fireworkIcon = fireworkIcon;
        Skills.Initialize(this);
        CreateHotbar();

        // Authored ground items become collectible; the starter stick is recreated each life.
        foreach (var root in gameObject.scene.GetRootGameObjects())
            if ((root.name == "Stick" || root.name == "Bulb" || root.name == "Gun") && !root.GetComponent<WorldLoot>())
                root.AddComponent<WorldLoot>().Initialize(root.name, 1, root.transform.position,
                                                          root.transform.position, this);
        SpawnStarterStick();

        // Enable overflow lighting and schedule, rather than immediately create, a random enemy.
        gameObject.AddComponent<WorldLightBudget>();
        ScheduleSpawn();
    }

    // Keep the self-click action separate from raw input so every equipped item follows the same priority.
    public bool TryOpenSkillsAt(Vector2 screen)
    {
        if (!playerSheet || OverHotbar(screen) || !playerSheet.ScreenHit(view, screen))
            return false;
        var menus = GetComponent<GameMenus>();
        return menus && menus.OpenSkills();
    }

    // Project a world heading onto camera axes to choose the visible sprite direction.
    public Vector2 ScreenDirection(Vector3 direction)
    {
        return new Vector2(Vector3.Dot(direction, view.transform.right), Vector3.Dot(direction, view.transform.up));
    }

    // Combat grows exponentially with distance; logarithmic clamping enforces the maximum multiplier.
    public float Growth(float distance)
    {
        return Mathf.Exp(Mathf.Min(Mathf.Log(Mathf.Max(1, maximumGrowthMultiplier)),
                                   Mathf.Max(0, distance) * exponentialGrowthPerMeter));
    }

    // Keep the original reward curve even when combat difficulty is tuned more aggressively.
    public float LootGrowth(float distance)
    {
        return Mathf.Exp(
            Mathf.Min(Mathf.Log(Mathf.Max(1, maximumGrowthMultiplier)), Mathf.Max(0, distance) * lootGrowthPerMeter));
    }

    // Progression uses horizontal distance, not height or accumulated walking distance.
    float DistanceFromSpawn => new Vector2(Player.position.x, Player.position.z).magnitude;

    // Reduce both ends of the random wait range without crossing their fastest allowed values.
    public Vector2 SpawnInterval(float distance)
    {
        float low =
            Mathf.Max(0.05f, fastestSpawnSeconds.x, startingSpawnSeconds.x - distance * spawnWaitReductionPerMeter);
        float high =
            Mathf.Max(low, fastestSpawnSeconds.y, startingSpawnSeconds.y - distance * spawnWaitReductionPerMeter);
        return new Vector2(low, high);
    }

    // Pick the next spawn time using the player's current distance from the origin.
    void ScheduleSpawn()
    {
        Vector2 interval = SpawnInterval(DistanceFromSpawn);
        nextSpawn = Time.time + Random.Range(interval.x, interval.y);
    }

    // Process one gameplay frame: movement, aim, attacks, animation, then timed spawning.
    void Update()
    {
        // Menus/death block input; regeneration still works when the running game loses focus.
        if (!Player || Dead || GameMenus.Blocking)
            return;
        AdvanceRegeneration(Time.deltaTime);
        if (!Application.isFocused && !Application.isBatchMode)
            return;

        // Device checks allow keyboard-only input or a temporarily disconnected mouse.
        var keyboard = Keyboard.current;
        var mouse = Mouse.current;
        // A physical right-click on Brighton takes priority over any equipped weapon or lamp action.
        if (mouse != null && mouse.rightButton.wasPressedThisFrame && TryOpenSkillsAt(mouse.position.ReadValue()))
            return;
        Vector2 movement = Vector2.zero;
        bool sprint = false;

        // Read remappable movement keys and keep arrow-key equipment cycling available.
        if (keyboard != null)
        {
            movement =
                new Vector2((keyboard[moveRightKey].isPressed ? 1 : 0) - (keyboard[moveLeftKey].isPressed ? 1 : 0),
                            (keyboard[moveUpKey].isPressed ? 1 : 0) - (keyboard[moveDownKey].isPressed ? 1 : 0));
            sprint = keyboard[sprintKey].isPressed || (sprintKey == Key.LeftCtrl && keyboard.rightCtrlKey.isPressed);
            if (keyboard.rightArrowKey.wasPressedThisFrame)
                CycleEquipment(1);
            if (keyboard.leftArrowKey.wasPressedThisFrame)
                CycleEquipment(-1);
        }

        // Wheel direction matches the arrow-key cycle and skips unavailable equipment.
        if (mouse != null)
        {
            float scroll = mouse.scroll.ReadValue().y;
            if (scroll > 0)
                CycleEquipment(-1);
            else if (scroll < 0)
                CycleEquipment(1);
        }

        // Flatten camera axes onto XZ; clamping prevents diagonal movement from being faster.
        Vector3 right = view.transform.right;
        right.y = 0;
        right.Normalize();
        Vector3 up = view.transform.forward;
        up.y = 0;
        up.Normalize();
        Vector3 step = Vector3.ClampMagnitude(right * movement.x + up * movement.y, 1);
        Vector3 before = Player.position;
        Player.position += step * (sprint ? sprintSpeed : walkSpeed) * Time.deltaTime;

        // Actor footprints are discs on the ground, independent of tilted visual meshes.
        foreach (var enemy in Enemies)
        {
            // Ignore removed enemies when pushing Brighton out of overlapping footprints.
            if (!enemy || !enemy.Alive)
                continue;
            Vector3 away = Player.position - enemy.transform.position;
            away.y = 0;
            float contact = playerRadius + enemyRadius;
            if (away.magnitude < contact)
                Player.position = enemy.transform.position +
                                  (away.sqrMagnitude > 0.00001f ? away.normalized : Vector3.back) * contact;
        }

        // Cursor facing is independent of movement, so backing up uses the correct animation row.
        bool moving = DirectionalSheet.IsMoving(Player.position - before, Time.deltaTime);
        if (mouse != null)
        {
            // Facing uses the visible character center so cursor direction remains intuitive during peeking.
            Vector2 playerScreen = view.WorldToScreenPoint(Player.position + Vector3.up * 0.25f);
            Vector2 screenAim = mouse.position.ReadValue() - playerScreen;
            if (screenAim.sqrMagnitude > 16)
            {
                // Invert the projection onto the XZ plane, including camera pitch.
                aim = (right * screenAim.x + up * (screenAim.y / Mathf.Max(0.1f, Mathf.Abs(view.transform.forward.y))))
                          .normalized;
            }

            // HUD clicks must not also fire a weapon or place a light in the world.
            if (!OverHotbar(mouse.position.ReadValue()))
            {
                if (MousePressed(mouse, attackMouseButton))
                    Attack();
                if (MousePressed(mouse, interactMouseButton))
                    Interact();
            }
        }

        // Bare arms are also used when a light, rather than a weapon, is equipped.
        playerSheet.Tick(ScreenDirection(aim), moving, sprint ? sprintFrameSeconds : walkFrameSeconds,
                         selected == 1   ? stickWalk
                         : selected == 2 ? gunWalk
                                         : armsWalk);

        // Even at the enemy cap, reschedule so spawns do not burst when a slot opens.
        if (Time.time >= nextSpawn)
        {
            if (Enemies.Count < maximumLivingEnemies)
                SpawnEnemy();
            ScheduleSpawn();
        }
    }

    // Convert the settings' button index into the corresponding input-system edge event.
    static bool MousePressed(Mouse mouse, int button) => button == 1   ? mouse.rightButton.wasPressedThisFrame
                                                         : button == 2 ? mouse.middleButton.wasPressedThisFrame
                                                                       : mouse.leftButton.wasPressedThisFrame;

    // Update feedback after movement, then smoothly peek toward the cursor without moving terrain.
    void LateUpdate()
    {
        UpdateVignette(Time.unscaledDeltaTime);
        RefreshHotbar();

        // No camera-follow work is needed before initialization or after death.
        if (!Player || Dead)
            return;

        // Normalize cursor position to a unit disc so diagonal peeking is not stronger.
        Vector2 cursor = Mouse.current != null ? Mouse.current.position.ReadValue()
                                               : new Vector2(Screen.width, Screen.height) * 0.5f;
        Vector2 offset =
            Vector2.ClampMagnitude(new Vector2(cursor.x / Screen.width * 2 - 1, cursor.y / Screen.height * 2 - 1), 1);
        Vector3 right = view.transform.right;
        right.y = 0;
        right.Normalize();
        Vector3 up = view.transform.forward;
        up.y = 0;
        up.Normalize();

        // Exponential smoothing makes peek response independent of the frame rate.
        Vector3 target = (right * offset.x + up * offset.y) * peekDistance;
        peek = Vector3.Lerp(peek, target, 1 - Mathf.Exp(-peekResponse * Time.deltaTime));
        view.transform.position = Player.position + cameraOffset + peek;
    }

    // Intersect a camera ray with Y=0 to turn a screen click into a ground location.
    bool GroundPoint(Vector2 screen, out Vector3 point)
    {
        Ray ray = view.ScreenPointToRay(screen);

        // Rays parallel to the plane do not produce a usable point.
        if (new Plane(Vector3.up, Vector3.zero).Raycast(ray, out float distance))
        {
            point = ray.GetPoint(distance);
            return true;
        }
        point = default;
        return false;
    }

    // Create an enemy just beyond a random screen edge, using difficulty at the player.
    void SpawnEnemy()
    {
        // Ground footprint of the actual camera view; spawn beyond an edge, including sprite height margin.
        Vector3 center = Player.position + peek;
        float y = Random.value;
        Vector2 edge;

        // Choose one of four edges and a random position along it.
        switch (Random.Range(0, 4))
        {
        case 0:
            edge = new Vector2(0, y * Screen.height);
            break;
        case 1:
            edge = new Vector2(Screen.width, y * Screen.height);
            break;
        case 2:
            edge = new Vector2(y * Screen.width, 0);
            break;
        default:
            edge = new Vector2(y * Screen.width, Screen.height);
            break;
        }

        // Failed projections are skipped; successful ones get extra space for sprite height.
        if (!GroundPoint(edge, out Vector3 point))
            return;
        Vector3 outward = (point - center).normalized;
        point += outward * (spawnOffscreenMeters + 1);
        Instantiate(darktonPrefab, point, darktonPrefab.transform.rotation)
            .AddComponent<DarktonAgent>()
            .Initialize(this, DistanceFromSpawn);
    }

    // Respect cooldowns, then fire a projectile or hit the nearest enemy in the melee arc.
    void Attack()
    {
        // Inventory changes can invalidate the selected weapon; fall back to bare hands.
        if (!CanEquip(selected))
            selected = 0;
        if (Time.time < nextAttack)
            return;

        // Light slots use bare arms and bare-hand attacks.
        float damage = selected == 1 ? stickDamage : selected == 2 ? gunDamage : unarmedDamage;
        nextAttack = Time.time + Mathf.Max(0.05f, selected == 1   ? stickCooldown
                                                  : selected == 2 ? gunCooldown
                                                                  : unarmedCooldown);

        // Gun attacks create traveling bullets instead of instantly damaging a target.
        if (selected == 2)
        {
            GameAudio.Play(GameAudio.Cue.Shoot);
            var bullet = new GameObject("Gun bullet").AddComponent<GunBullet>();
            bullet.transform.position = Player.position + Vector3.up * bulletHeight;
            bullet.Initialize(this, aim);
            return;
        }

        // Melee range starts at the player footprint and adds the weapon's pixel-based reach.
        float range = playerRadius + playerSheet.PixelSize * (selected == 1 ? stickRangePixels : unarmedRangePixels);
        DarktonAgent closest = null;
        float nearest = float.PositiveInfinity;

        // Search living enemies inside both the reach and facing cone; hit only the closest.
        foreach (var enemy in Enemies)
        {
            if (!enemy || !enemy.Alive)
                continue;
            Vector3 delta = enemy.transform.position - Player.position;
            delta.y = 0;
            bool hit = delta.magnitude <= range + enemyRadius && Vector3.Angle(aim, delta) <= meleeHalfAngle;
            if (hit && delta.sqrMagnitude < nearest)
            {
                nearest = delta.sqrMagnitude;
                closest = enemy;
            }
        }
        if (closest)
            closest.Hit(damage);
    }

    // Right-click places a selected light, or raises the optional nearby-item interaction hook.
    void Interact()
    {
        // Only clicks that intersect the ground can interact with world objects.
        if (!GroundPoint(Mouse.current.position.ReadValue(), out Vector3 point))
            return;

        // Lamp placement handles its own shorter clamped range and resource spending.
        if (selected >= 3)
        {
            TryPlaceLight(point);
            return;
        }
        if (Vector3.Distance(Player.position, point) > interactionRange)
            return;

        // Optional interaction hook; actual pickups use the feet-centered magnet.
        WorldLoot nearest = null;
        float best = 0.4f;

        // Choose the item nearest the clicked point, not nearest Brighton.
        foreach (var loot in FindObjectsByType<WorldLoot>(FindObjectsSortMode.None))
        {
            float distance = Vector3.Distance(loot.transform.position, point);
            if (distance < best)
            {
                best = distance;
                nearest = loot;
            }
        }
        if (nearest)
            InteractedWithLoot?.Invoke(nearest);
    }

    // Heal in single ticks; pause/full health must not accumulate a bank of future healing.
    public void AdvanceRegeneration(float seconds)
    {
        // Ignore invalid time and reset the timer whenever health is already full.
        if (Dead || seconds <= 0)
            return;
        if (health >= maximumHealth)
        {
            regenerationElapsed = 0;
            return;
        }

        // Only complete one interval per call, even after an unusually long frame.
        regenerationElapsed += seconds;
        float interval = Mathf.Max(0.1f, regenerationSeconds);
        if (regenerationElapsed < interval)
            return;

        // Never repay multiple healing ticks after a long frame or a clock jump.
        regenerationElapsed = 0;
        health = Mathf.Min(maximumHealth, health + Mathf.Max(0, regenerationHealth));
        if (health >= maximumHealth)
            regenerationElapsed = 0;
    }

    // Clamp incoming damage, timestamp the heartbeat, and freeze simulation on a lethal hit.
    public void TakeDamage(float damage)
    {
        // Further hits cannot change a life that has already ended.
        if (Dead)
            return;
        health = Mathf.Max(0, health - Mathf.Max(0, damage));
        // Lethal hits use the death clip instead of layering a hit clip over it.
        if (damage > 0)
            GameAudio.Play(health <= 0 ? GameAudio.Cue.BrightonDead : GameAudio.Cue.BrightonHit);
        lastDamage = Time.unscaledTime;
        if (health <= 0)
        {
            Dead = true;
            if (Skills)
                Skills.ResetProgress();
            Time.timeScale = 0;
        }
    }

    // Reset this life while intentionally preserving placed lamps and ordinary ground loot.
    public void Respawn()
    {
        if (Skills)
            Skills.ResetProgress();
        if (GameAudio.Instance)
            GameAudio.Instance.ResetWorldSounds();
        // Replace starter loot, but restore ordinary items interrupted during magnet pickup.
        foreach (var loot in FindObjectsByType<WorldLoot>(FindObjectsSortMode.None))
            if (loot.IsStarter)
            {
                loot.gameObject.SetActive(false);
                Destroy(loot.gameObject);
            }
            else
                loot.CancelPickup();

        // Clear inventory, projectiles, and enemies; deactivate objects before deferred destruction.
        Inventory.Clear();
        foreach (var bullet in FindObjectsByType<GunBullet>(FindObjectsSortMode.None))
            bullet.Remove();
        foreach (var enemy in Enemies.ToArray())
            if (enemy)
            {
                enemy.gameObject.SetActive(false);
                Destroy(enemy.gameObject);
            }
        Enemies.Clear();

        // Restore player state, camera, health feedback, and attack/healing timers at the origin.
        Player.position = Vector3.zero;
        health = maximumHealth;
        Dead = false;
        selected = 0;
        peek = Vector3.zero;
        regenerationElapsed = 0;
        displayedMissingHealth = 0;
        aim = Vector3.back;
        nextAttack = 0;
        lastDamage = -100;
        Time.timeScale = 1;
        view.transform.position = cameraOffset;

        // A fresh life gets the initial spawn interval and a new nearby first weapon.
        ScheduleSpawn();
        SpawnStarterStick();
    }

    // Roll a resource type and base quantity, then grow expected quantity with distance.
    public void DropLoot(Vector3 position)
    {
        // Each resource rolls independently; probabilities above 100% grant full rolls plus the remainder.
        DropResource(position, stickLootPrefab, "Stick", stickDropChance);
        DropResource(position, bulbLootPrefab, "Bulb", bulbDropChance);
    }

    public static int DropRolls(float chance, float roll)
    {
        chance = Mathf.Max(0, chance);
        int full = Mathf.FloorToInt(chance);
        return full + (roll < chance - full ? 1 : 0);
    }

    void DropResource(Vector3 position, GameObject prefab, string item, float chance)
    {
        if (!prefab)
            return;
        int rolls = DropRolls(chance, Random.value);
        for (int roll = 0; roll < rolls; roll++)
            DropResourceRoll(position, prefab, item);
    }

    void DropResourceRoll(Vector3 position, GameObject prefab, string item)
    {
        float amount = (Random.value < twoItemChance ? 2 : 1) * LootGrowth(DistanceFromSpawn);

        // Stochastic rounding preserves a smooth expected reward at fractional multipliers.
        int quantity =
            Mathf.Clamp(Mathf.FloorToInt(amount) + (Random.value < amount % 1 ? 1 : 0), 1, maximumDropQuantity);

        // Missing optional loot assets should not prevent enemy death cleanup.
        if (!prefab)
            return;

        // One object per item makes quantity visible without adding inventory UI.
        for (int i = 0; i < quantity; ++i)
        {
            Vector2 scatter = Random.insideUnitCircle * 0.5f;
            var drop = Instantiate(prefab, position, prefab.transform.rotation);
            drop.AddComponent<WorldLoot>().Initialize(item, 1, position,
                                                      position + new Vector3(scatter.x, 0, scatter.y), this);
        }
    }

    // Prevent HUD equipment clicks from falling through to world attack/interaction handling.
    bool OverHotbar(Vector2 mouse)
    {
        // Input arrives in screen pixels; HUD layout uses reference-resolution units.
        float scale = hotbarCanvas ? Mathf.Max(0.001f, hotbarCanvas.scaleFactor) : 1;
        mouse = new Vector2(mouse.x / scale, (Screen.height - mouse.y) / scale);

        // The visible row is compacted, so its width depends on currently available equipment.
        int available = 0;
        for (int i = 0; i < 5; i++)
            if (CanEquip(i))
                available++;
        return mouse.x >= 16 && mouse.x < 16 + available * InventoryHud.EquipmentSpacing && mouse.y >= 16 &&
               mouse.y < 16 + InventoryHud.SlotHeight;
    }

    // Draw immediate-mode health feedback; the inventory itself is a separate canvas.
    void OnGUI()
    {
        if (!Player)
            return;
        DrawVignette();
    }

    // Turn missing health into a tint amount, smoothing healing but not new damage.
    public void UpdateVignette(float seconds)
    {
        float target = Mathf.Clamp01(1 - health / Mathf.Max(0.01f, maximumHealth));

        // Damage reads immediately; healing fades gradually, including the last HP.
        displayedMissingHealth =
            target >= displayedMissingHealth
                ? target
                : Mathf.MoveTowards(displayedMissingHealth, target,
                                    Mathf.Max(0, seconds) /
                                        (Mathf.Max(1, maximumHealth) * Mathf.Max(0.05f, vignetteFadeSeconds)));
    }

    // Cache a radial alpha mask, then tint it according to health and the latest heartbeat.
    void DrawVignette()
    {
        // Rebuild only when the screen dimensions change or the mask has not been created yet.
        if (!vignette || vignetteWidth != Screen.width || vignetteHeight != Screen.height)
        {
            if (vignette)
                Destroy(vignette);
            vignetteWidth = Screen.width;
            vignetteHeight = Screen.height;
            vignette = new Texture2D(256, 256, TextureFormat.RGBA32, false);
            vignette.wrapMode = TextureWrapMode.Clamp;
            var colors = new Color[256 * 256];
            float aspect = (float)Screen.width / Mathf.Max(1, Screen.height);
            float radius = Mathf.Max(vignetteCenterRadius, 0.65f / (2 * view.orthographicSize));

            // Normalize each pixel with aspect correction; keep the central area less opaque.
            for (int y = 0; y < 256; y++)
                for (int x = 0; x < 256; x++)
                {
                    float distance = new Vector2((x / 255f - 0.5f) * aspect, y / 255f - 0.5f).magnitude;
                    float fade =
                        Mathf.SmoothStep(0, 1, Mathf.InverseLerp(radius, Mathf.Max(radius + 0.01f, 0.5f), distance));
                    colors[y * 256 + x] = new Color(0.5f, 0, 0, Mathf.Lerp(0.5f, 1, fade));
                }
            vignette.SetPixels(colors);
            vignette.Apply();
        }

        // A short decaying sine pulse reacts to damage even while game time is frozen.
        float age = Time.unscaledTime - lastDamage;
        float pulse = age < heartbeatDuration && !Dead
                          ? heartbeatStrength * Mathf.Sin(age * Mathf.PI * 5) * (1 - age / heartbeatDuration)
                          : 0;
        GUI.color = new Color(1, 1, 1, Mathf.Clamp01(displayedMissingHealth + pulse));
        GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), vignette);
        GUI.color = Color.white;
    }

    // Build a corner-anchored HUD scaled uniformly from the 1440p reference resolution.
    void CreateHotbar()
    {
        hotbarCanvas = new GameObject("Gameplay UI", typeof(RectTransform), typeof(Canvas)).GetComponent<Canvas>();
        hotbarCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
        hotbarCanvas.sortingOrder = 2000;
        var scaler = hotbarCanvas.gameObject.AddComponent<UnityEngine.UI.CanvasScaler>();
        scaler.uiScaleMode = UnityEngine.UI.CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(2560, 1440);
        scaler.screenMatchMode = UnityEngine.UI.CanvasScaler.ScreenMatchMode.Expand;
        inventoryHud = hotbarCanvas.gameObject.AddComponent<InventoryHud>();
        inventoryHud.Initialize(this);
        RefreshHotbar();
    }

    // Keep equipment/counts current but hide the entire HUD behind blocking menus.
    void RefreshHotbar()
    {
        if (!hotbarCanvas)
            return;
        hotbarCanvas.enabled = !Dead && !GameMenus.Blocking;
        inventoryHud.Refresh();
    }

    // Unarmed is always valid; weapons require ownership and lamps require an unlocked recipe.
    public bool CanEquip(int item)
    {
        if (item == 0)
            return true;
        if (!Inventory)
            return false;
        if (item == 1)
            return Inventory.Count("Stick") > 0;
        if (item == 2)
            return Inventory.HasGun;
        if (item == 3)
            return stickBulbUnlocked && Inventory.CanCraft(1);
        return item == 4 && tallLightUnlocked && Inventory.CanCraft(5);
    }

    // Losing the last required resource unequips that slot and refreshes its HUD visibility.
    void InventoryChanged()
    {
        if (!CanEquip(selected))
            selected = 0;
        if (inventoryHud)
            inventoryHud.Refresh();
    }

    // Wrap across five slots, skipping items the player cannot currently use.
    public void CycleEquipment(int direction)
    {
        int previous = selected;
        // At most five attempts are needed because unarmed is always an available fallback.
        for (int i = 0; i < 5; i++)
        {
            selected = (selected + (direction < 0 ? 4 : 1)) % 5;
            if (CanEquip(selected))
                break;
        }
        InventoryChanged();

        // Arrow/wheel changes use the shared button sound, but an unchanged slot stays silent.
        if (selected != previous)
            GameAudio.Play(GameAudio.Cue.ButtonPress);
    }

    // Clamp lamp placement to its allowed radius and spend the recipe only after creation.
    public bool TryPlaceLight(Vector3 point)
    {
        // Refuse placement during menus/death or when this slot is no longer affordable.
        if (Dead || GameMenus.Blocking || selected < 3 || !CanEquip(selected))
            return false;
        int cost = selected == 3 ? 1 : 5;
        var prefab = selected == 3 ? stickBulbPrefab : tallLightPrefab;
        if (!prefab)
            return false;

        // Work only on the ground plane; distant clicks place at the nearest allowed edge.
        Vector3 offset = point - Player.position;
        offset.y = 0;
        point =
            Player.position + Vector3.ClampMagnitude(offset, lightPlacementVoxels * Mathf.Abs(playerSheet.PixelSize));
        point.y = 0;
        var placed = Instantiate(prefab, point, prefab.transform.rotation);

        // If spending unexpectedly fails, undo the new object rather than grant a free lamp.
        if (!Inventory.SpendRecipe(cost))
        {
            Destroy(placed);
            return false;
        }
        GameAudio.Play(GameAudio.Cue.Place);
        TryDropGun(placed);
        return true;
    }

    // Increase current HP along with capacity so a health upgrade does not create missing-health tint.
    public void AddHealthCapacity(float amount)
    {
        maximumHealth += amount;
        health = Mathf.Min(maximumHealth, health + amount);
    }

    void TryDropGun(GameObject lamp)
    {
        if (!Skills || Skills.UnlockedCount < 3 || Inventory.HasGun || !gunLootPrefab ||
            Random.value >= gunDropOnLightChance)
            return;
        var light = lamp.GetComponentInChildren<Light>();
        if (!light || light.intensity <= 0)
            return;

        // Sample the inner ground intersection of the light sphere, accounting for its elevation.
        Vector3 center = light.transform.position;
        float radiusSquared = light.range * light.range - center.y * center.y;
        if (radiusSquared <= 0)
            return;
        Vector2 offset = Random.insideUnitCircle * Mathf.Sqrt(radiusSquared) * 0.75f;
        Vector3 destination = new Vector3(center.x + offset.x, 0, center.z + offset.y);
        var drop = Instantiate(gunLootPrefab, destination, gunLootPrefab.transform.rotation);
        drop.AddComponent<WorldLoot>().Initialize("Gun", 1, lamp.transform.position, destination, this);
    }

    // Put an initial weapon near the capsule, just outside the automatic pickup radius.
    void SpawnStarterStick()
    {
        if (!stickLootPrefab)
            return;

        // Nearby and lit by the spawn bulb, but outside automatic pickup range:
        // a new life starts empty until Brighton actually approaches the stick.
        Vector3 position = PickupOrigin + Vector3.right * Mathf.Max(0.35f, PickupRadius + 1f / 32);
        position.y = 0;
        var starter = Instantiate(stickLootPrefab, position, stickLootPrefab.transform.rotation);
        starter.name = "Starter Stick";
        var loot = starter.AddComponent<WorldLoot>();
        loot.IsStarter = true;
        loot.Initialize("Stick", 1, position, position, this);
    }

    // Locate the visible head, then create a game-font popup independent of the enemy's lifetime.
    public void ShowEnemyDamage(Transform enemy, float amount)
    {
        if (!hotbarCanvas || !view || amount <= 0)
            return;
        Vector3 head = enemy.position + Vector3.up * 0.8f;

        // Mesh bounds locate the top of the sprite; ignore invisible shadow proxies.
        foreach (var renderer in enemy.GetComponentsInChildren<MeshRenderer>())
        {
            if (renderer.shadowCastingMode == UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly)
                continue;
            var filter = renderer.GetComponent<MeshFilter>();
            if (!filter || !filter.sharedMesh)
                continue;
            Bounds bounds = filter.sharedMesh.bounds;
            head = renderer.transform.TransformPoint(bounds.center + Vector3.up * bounds.extents.y);
            break;
        }

        // Nudge above the head in screen-up space and attach the popup to the scaled HUD.
        head += view.transform.up * 0.08f;
        var popup = new GameObject("Damage number", typeof(RectTransform)).AddComponent<FloatingDamageNumber>();
        popup.transform.SetParent(hotbarCanvas.transform, false);
        popup.Initialize(view, head, amount, damageNumberSeconds, damageNumberFontSize, damageNumberRisePixels,
                         damageNumberColor, defaultFont);
    }

    // Release generated UI textures/canvas and restore time when a dead scene is removed.
    void OnDestroy()
    {
        if (Dead)
            Time.timeScale = 1;
        if (vignette)
            Destroy(vignette);
        if (hotbarCanvas)
            Destroy(hotbarCanvas.gameObject);
    }
}
