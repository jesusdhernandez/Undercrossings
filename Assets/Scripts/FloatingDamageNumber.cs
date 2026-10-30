using System.Globalization;
using UnityEngine;
using UnityEngine.UI;

// Displays one hit's damage in the game's font, above a fixed world-space point.
// The popup belongs to the HUD, so killing its enemy does not remove the number.
public sealed class FloatingDamageNumber : MonoBehaviour
{
    public string Number { get; private set; }

    Camera view;
    Vector3 worldAnchor;
    Color tint;
    float age, lifetime, rise;
    RectTransform rectTransform;
    Text fontText;

    public void Initialize(Camera camera, Vector3 anchor, float amount, float duration, int fontSize, float travel,
                           Color textColor, Font font)
    {
        // Save the hit position and animation settings; omit trailing decimal zeros.
        view = camera;
        worldAnchor = anchor;
        Number = amount.ToString("0.##", CultureInfo.InvariantCulture);
        lifetime = Mathf.Max(0.05f, duration);
        rise = travel;
        tint = textColor;

        // Use bottom-left HUD coordinates so screen projection works at any resolution.
        rectTransform = GetComponent<RectTransform>();
        rectTransform.anchorMin = rectTransform.anchorMax = Vector2.zero;
        rectTransform.pivot = new Vector2(0.5f, 0);
        rectTransform.sizeDelta = new Vector2(180, 48);

        // Only create text when the game font is assigned; there is no custom-font fallback.
        if (font)
        {
            // Parent a standard UI text object to this popup, separate from the enemy.
            fontText = new GameObject("Number", typeof(RectTransform)).AddComponent<Text>();
            fontText.transform.SetParent(transform, false);

            // Center the damage amount and leave room for larger upgraded hits.
            fontText.rectTransform.sizeDelta = new Vector2(180, 48);
            fontText.font = font;
            fontText.fontSize = Mathf.Max(1, fontSize);
            fontText.text = Number;
            fontText.alignment = TextAnchor.MiddleCenter;
            fontText.horizontalOverflow = HorizontalWrapMode.Overflow;

            // Apply the light-gray tint without intercepting gameplay clicks.
            fontText.color = tint;
            fontText.raycastTarget = false;
        }

        Position();
    }

    void LateUpdate()
    {
        // Scaled time freezes this animation when a menu or death pauses the world.
        age += Time.deltaTime;
        if (age >= lifetime || !view)
        {
            Destroy(gameObject);
            return;
        }

        // Hold the initial opacity briefly, then fade out for the rest of the lifetime.
        float progress = age / lifetime;
        if (fontText)
        {
            float opacity = 1 - Mathf.InverseLerp(0.15f, 1, progress);
            fontText.color = new Color(tint.r, tint.g, tint.b, tint.a * opacity);
        }

        Position();
    }

    void Position()
    {
        // A missing camera cannot project the saved hit location onto the HUD.
        if (!view)
            return;

        Vector3 screen = view.WorldToScreenPoint(worldAnchor);

        // Hide anchors behind the camera instead of mirroring them onto the screen.
        if (fontText)
            fontText.enabled = screen.z > 0;

        // Convert screen pixels to canvas units, then add a gently slowing upward drift.
        float lift = rise * Mathf.Sin(Mathf.Clamp01(age / lifetime) * Mathf.PI * 0.5f);
        var parent = rectTransform.parent as RectTransform;
        if (parent && RectTransformUtility.ScreenPointToLocalPointInRectangle(parent, screen, null, out Vector2 local))
        {
            Vector2 position = local - parent.rect.min + Vector2.up * lift;
            rectTransform.anchoredPosition = new Vector2(Mathf.Round(position.x), Mathf.Round(position.y));
        }
    }
}
