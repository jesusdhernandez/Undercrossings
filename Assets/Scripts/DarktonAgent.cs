using UnityEngine;

// Controls one Darkton: chase Brighton, avoid overlapping peers, and attack on contact.
public sealed class DarktonAgent : MonoBehaviour
{
    WorldsidesGameplay game;
    DirectionalSheet animationSheet;
    float health, damage, cooldown, nextHit;
    public bool Alive => health > 0;

    // Lock in distance-based combat stats at spawn, then register this enemy with the game.
    public void Initialize(WorldsidesGameplay owner, float distance)
    {
        game = owner;
        float growth = game.Growth(distance);
        health = game.enemyHealth * growth;
        damage = game.enemyDamage * growth;

        // Double attack frequency every ten meters too, stopping at the authored minimum cooldown.
        cooldown = Mathf.Max(game.minimumEnemyCooldown, game.enemyCooldown / growth);

        // Enemy artwork already includes arms, so it does not need an equipment overlay.
        animationSheet = gameObject.AddComponent<DirectionalSheet>();
        animationSheet.Initialize(game.darktonWalk, false);
        animationSheet.Step += () => GameAudio.Play(GameAudio.Cue.DarktonStep, transform.position);
        game.Enemies.Add(this);
    }

    // Run AI only during active gameplay; all movement stays on the XZ ground plane.
    void Update()
    {
        if (!game || game.Dead || GameMenus.Blocking || !Alive)
            return;

        // Approach the player but stop when their circular ground footprints touch.
        Vector3 delta = game.Player.position - transform.position;
        delta.y = 0;
        float contact = game.playerRadius + game.enemyRadius;
        Vector3 before = transform.position;
        transform.position += delta.normalized * Mathf.Min(game.walkSpeed * game.enemySpeedMultiplier * Time.deltaTime,
                                                           Mathf.Max(0, delta.magnitude - contact));

        // Disc separation avoids all enemies collapsing into a single sprite.
        foreach (var other in game.Enemies)
        {
            // Ignore this enemy and removed enemies before measuring separation.
            if (other == this || !other || !other.Alive)
                continue;
            Vector3 away = transform.position - other.transform.position;
            away.y = 0;
            float separation = game.enemyRadius * 2;

            // Move halfway out of an overlap so both enemies can share the correction.
            if (away.sqrMagnitude > 0.00001f && away.magnitude < separation)
                transform.position += away.normalized * (separation - away.magnitude) * 0.5f;
        }

        // Animate actual movement; when blocked, keep facing the player without walking.
        Vector3 motion = transform.position - before;
        bool moving = DirectionalSheet.IsMoving(motion, Time.deltaTime);

        // Normalize before projecting: facing must not disappear when one frame's displacement is tiny.
        Vector3 facing = (moving ? motion : delta).normalized;
        animationSheet.Tick(game.ScreenDirection(facing), moving, game.enemyFrameSeconds);

        // Recheck contact after separation and allow only one hit per cooldown.
        delta = game.Player.position - transform.position;
        delta.y = 0;
        if (delta.magnitude <= contact + 0.015f && Time.time >= nextHit)
        {
            nextHit = Time.time + cooldown;
            game.TakeDamage(damage);
        }
    }

    // Show each valid hit, then drop loot and remove the enemy only after lethal damage.
    public void Hit(float amount)
    {
        if (!Alive || amount <= 0)
            return;
        GameAudio.Play(GameAudio.Cue.DarktonHit);
        game.ShowEnemyDamage(transform, amount);
        health -= amount;
        if (health > 0)
            return;

        // Deactivate immediately because Destroy does not remove the object until frame end.
        game.DropLoot(transform.position);
        game.Enemies.Remove(this);
        gameObject.SetActive(false);
        Destroy(gameObject);
    }

    // Also unregister enemies removed by scene cleanup rather than combat.
    void OnDestroy()
    {
        if (game)
            game.Enemies.Remove(this);
    }
}
