using UnityEngine;
using UnityEngine.Rendering;

// Moves a glowing projectile and tests its full path against enemy ground footprints.
public sealed class GunBullet : MonoBehaviour
{
    WorldsidesGameplay game;
    Vector3 direction;
    float remaining, damage, speed, radius;
    MeshRenderer visualRenderer;
    Light bulletLight;
    MaterialPropertyBlock properties;
    public bool IsFading { get; private set; }
    public const float FadeSeconds = 0.15f;
    public ShotStats Stats { get; private set; }

    // Copy every projectile/light setting at firing time so firework descendants inherit the original shot.
    public struct ShotStats
    {
        public float range, damage, speed, radius, length, width, height, lightHeight, lightIntensity, lightRange;
        public float fireworkChance;
        public Color lightColor;
        public Material material;
    }

    public static ShotStats Capture(WorldsidesGameplay game)
    {
        return new ShotStats { range = game.gunRange,
                               damage = game.gunDamage,
                               speed = game.bulletSpeed,
                               radius = game.bulletCollisionRadius,
                               length = game.bulletLength,
                               width = game.bulletWidth,
                               height = game.bulletHeight,
                               lightHeight = game.bulletLightHeight,
                               lightIntensity = game.bulletLightIntensity,
                               lightRange = game.bulletLightRange,
                               lightColor = game.bulletLightColor,
                               material = game.bulletMaterial,
                               fireworkChance = game.Skills ? game.Skills.FireworkChance : 0 };
    }

    // Snapshot weapon stats so later upgrades do not change a bullet already in flight.
    public void Initialize(WorldsidesGameplay owner, Vector3 heading)
    {
        Initialize(owner, heading, Capture(owner));
    }

    public void Initialize(WorldsidesGameplay owner, Vector3 heading, ShotStats stats)
    {
        game = owner;
        Stats = stats;
        direction = heading;
        direction.y = 0;
        direction.Normalize();
        remaining = Mathf.Max(0, stats.range);
        damage = stats.damage;
        speed = Mathf.Max(0.1f, stats.speed);
        radius = Mathf.Max(0, stats.radius);

        // Build a flat visual with no physics collider; collision is handled by the sweep below.
        var visual = GameObject.CreatePrimitive(PrimitiveType.Quad);
        visual.name = "Bullet sprite";
        visual.transform.SetParent(transform, false);
        var collider = visual.GetComponent<Collider>();
        collider.enabled = false;
        Destroy(collider);
        var renderer = visual.GetComponent<MeshRenderer>();
        visualRenderer = renderer;
        renderer.sharedMaterial = stats.material;
        properties = new MaterialPropertyBlock();
        properties.SetFloat("_Fade", 1);

        // Prefer the supplied texture, or let the shader use its orange fallback color.
        if (stats.material)
        {
            properties.SetFloat("_UseTexture", stats.material.GetTexture("_BaseMap") ? 1 : 0);
        }
        renderer.SetPropertyBlock(properties);
        renderer.shadowCastingMode = ShadowCastingMode.Off;
        renderer.receiveShadows = false;
        visual.transform.localScale = new Vector3(stats.length, stats.width, 1);

        // Face the camera and rotate the bullet to match its projected travel direction.
        var camera = Camera.main;
        if (camera)
        {
            Vector2 screenDirection = game.ScreenDirection(direction);
            visual.transform.rotation =
                camera.transform.rotation *
                Quaternion.Euler(0, 0, Mathf.Atan2(screenDirection.y, screenDirection.x) * Mathf.Rad2Deg);
        }

        // Attach a small, shadow-free point light that travels with the bullet.
        var lightObject = new GameObject("Bullet light");
        lightObject.transform.SetParent(transform, false);
        lightObject.transform.localPosition = Vector3.up * (stats.lightHeight - stats.height);
        var light = lightObject.AddComponent<Light>();
        bulletLight = light;
        light.type = LightType.Point;
        light.color = stats.lightColor;
        light.intensity = Mathf.Max(0, stats.lightIntensity);
        light.range = Mathf.Max(0.01f, stats.lightRange);
        light.shadows = LightShadows.None;
        UpdateFade();
    }

    // Scaled delta time naturally freezes bullets while gameplay is paused.
    void Update()
    {
        Advance(Time.deltaTime);
    }

    // Sweep the whole traveled segment, rather than testing just the end of a fast-moving bullet.
    public void Advance(float seconds)
    {
        if (!game || game.Dead || !gameObject.activeSelf || seconds <= 0)
            return;

        // Expired bullets disappear at the endpoint rather than waiting there to fade.
        if (remaining <= 0)
        {
            Remove();
            return;
        }

        // Limit this frame's movement to the remaining weapon range.
        float travel = Mathf.Min(remaining, speed * seconds);
        DarktonAgent target = null;
        float nearest = travel;

        // Find the first intersection between the travel segment and any living enemy disc.
        foreach (var enemy in game.Enemies)
        {
            // Destroyed or dead enemies must not absorb new bullets.
            if (!enemy || !enemy.Alive)
                continue;
            Vector3 delta = enemy.transform.position - transform.position;
            delta.y = 0;
            float reach = game.enemyRadius + radius;

            // Project onto the path; the perpendicular distance tells us whether the disc is crossed.
            float along = Vector3.Dot(delta, direction);
            float perpendicularSquared = delta.sqrMagnitude - along * along;

            // Discs outside the swept radius cannot be hit.
            if (perpendicularSquared > reach * reach)
                continue;

            // The chord gives the entry/exit distances along the bullet's straight path.
            float halfChord = Mathf.Sqrt(Mathf.Max(0, reach * reach - perpendicularSquared));

            // An exit behind the bullet means the entire enemy is already behind it.
            if (along + halfChord < 0)
                continue;
            float entry = Mathf.Max(0, along - halfChord);

            // Keep the nearest entry, including enemies overlapping the bullet's start.
            if (entry <= nearest)
            {
                nearest = entry;
                target = enemy;
            }
        }

        // Stop at the first hit; otherwise expire after using up the maximum travel range.
        transform.position += direction * nearest;
        remaining -= nearest;
        if (target)
        {
            Vector3 deathPosition = target.transform.position;
            target.Hit(damage);
            if (!target.Alive && Random.value < Stats.fireworkChance)
                EmitFireworks(deathPosition);
            Remove();
        }
        else
        {
            UpdateFade();
            if (remaining <= 0.0001f)
                Remove();
        }
    }

    // Start fading 0.15 seconds BEFORE arrival, keeping the original opacity loss per second.
    void UpdateFade()
    {
        // A flight shorter than the fade duration starts partially faded rather than fading faster.
        float opacity = Mathf.Clamp01(remaining / (speed * FadeSeconds));
        IsFading = opacity < 1;
        properties.SetFloat("_Fade", opacity);
        visualRenderer.SetPropertyBlock(properties);
        bulletLight.intensity = Mathf.Max(0, Stats.lightIntensity) * opacity;
    }

    void EmitFireworks(Vector3 position)
    {
        // Dead targets are already unregistered. These eight friendly bullets can only hit other Darktons.
        for (int i = 0; i < 8; i++)
        {
            float angle = i * Mathf.PI / 4;
            Vector3 heading = new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle));
            var bullet = new GameObject("Firework bullet").AddComponent<GunBullet>();
            bullet.transform.position = position + Vector3.up * Stats.height;
            bullet.Initialize(game, heading, Stats);
        }
    }

    // Disable the visual/light immediately, then schedule object destruction.
    public void Remove()
    {
        gameObject.SetActive(false);
        Destroy(gameObject);
    }
}
