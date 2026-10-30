using UnityEngine;
using UnityEngine.UI;

// Builds the equipment row and material counts on the resolution-scaled gameplay canvas.
public sealed class InventoryHud : MonoBehaviour
{
    // These dimensions are canvas units at the 2560x1440 reference resolution.
    public const float EquipmentSpacing = 128, MaterialSpacing = 112, SlotHeight = 164;
    WorldsidesGameplay game;
    readonly RectTransform[] equipment = new RectTransform[5];
    readonly RectTransform[] materials = new RectTransform[2];
    readonly Text[] counts = new Text[2];
    readonly Text[] equipmentNames = new Text[5];
    readonly string[] names = { "Unarmed", "Stick", "Gun", "Stick Bulb", "Tall Light" };

    // Create slots once; Refresh only changes availability, selection, positions, and counts.
    public void Initialize(WorldsidesGameplay owner)
    {
        game = owner;
        var top = Rect("Hotbar", transform, Vector2.zero, new Vector2(0, 1), new Vector2(0, 1));

        // Reuse the dropped bulb's texture so the HUD matches the in-world item.
        Texture2D bulb = null;
        var bulbRenderer = game.bulbLootPrefab ? game.bulbLootPrefab.GetComponentInChildren<MeshRenderer>() : null;

        // A missing loot prefab leaves a blank bulb icon rather than causing a null reference.
        if (bulbRenderer)
            bulb = bulbRenderer.sharedMaterial.mainTexture as Texture2D;
        Texture2D[] textures = { game.brightonWalk, game.stickIcon, game.gunIcon, game.smallLightIcon,
                                 game.tallLightIcon };

        // Equipment includes unarmed plus weapons and craftable lights, with selected-only labels.
        for (int i = 0; i < 5; i++)
        {
            equipment[i] = Slot(top, names[i], textures[i], false, i == 0);
            equipmentNames[i] = TextLabel(equipment[i], names[i]);
            equipment[i].gameObject.AddComponent<CanvasGroup>();
        }

        // Anchor resource slots to the bottom-right; weapon ownership is not shown here.
        var bottom = Rect("Crafting items", transform, Vector2.zero, new Vector2(1, 0), new Vector2(1, 0));
        materials[0] = Slot(bottom, "Stick", game.stickIcon, true, false);
        materials[1] = Slot(bottom, "Bulb", bulb, true, false);
        counts[0] = TextLabel(materials[0], "");
        counts[1] = TextLabel(materials[1], "");
        Refresh();
    }

    // Create a UI rectangle with identical minimum/maximum anchors, so it stays corner-aligned.
    RectTransform Rect(string name, Transform parent, Vector2 size, Vector2 anchor, Vector2 pivot)
    {
        var r = new GameObject(name, typeof(RectTransform)).GetComponent<RectTransform>();
        r.SetParent(parent, false);
        r.anchorMin = r.anchorMax = anchor;
        r.pivot = pivot;
        r.sizeDelta = size;
        return r;
    }

    // Build a 96-pixel icon and a crisp offset shadow, without a background panel.
    RectTransform Slot(Transform parent, string name, Texture2D texture, bool bottom, bool sheet)
    {
        var slot = Rect(name, parent, new Vector2(bottom ? MaterialSpacing : EquipmentSpacing, SlotHeight),
                        bottom ? new Vector2(1, 0) : new Vector2(0, 1), bottom ? new Vector2(1, 0) : new Vector2(0, 1));
        var icon = Rect("Icon", slot, new Vector2(96, 96), new Vector2(0.5f, 1), new Vector2(0.5f, 1))
                       .gameObject.AddComponent<RawImage>();
        icon.texture = texture;
        icon.raycastTarget = false;

        // The unarmed icon takes one cell from Brighton's walk sheet instead of the whole sheet.
        if (sheet)
            icon.uvRect = new UnityEngine.Rect(0, 0.75f, 0.25f, 0.25f);
        var shadow = icon.gameObject.AddComponent<Shadow>();
        shadow.effectDistance = new Vector2(3, -3);
        shadow.effectColor = new Color(0, 0, 0, 0.85f);
        return slot;
    }

    // Create non-interactive, doubled-size game-font text beneath an icon.
    Text TextLabel(Transform parent, string content)
    {
        var text = Rect("Label", parent, new Vector2(224, 60), new Vector2(0.5f, 1), new Vector2(0.5f, 1))
                       .gameObject.AddComponent<Text>();
        text.rectTransform.anchoredPosition = new Vector2(0, -100);
        text.font = game.defaultFont ? game.defaultFont : Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        text.fontSize = 44;
        text.alignment = TextAnchor.UpperCenter;
        text.text = content;
        text.horizontalOverflow = HorizontalWrapMode.Overflow;
        text.raycastTarget = false;
        text.color = Color.white;
        var shadow = text.gameObject.AddComponent<Shadow>();
        shadow.effectDistance = new Vector2(2, -2);
        shadow.effectColor = Color.black;
        return text;
    }

    // Repack visible equipment without gaps and update the resource counts in acquisition order.
    public void Refresh()
    {
        // Initialization can run before inventory exists; wait until gameplay supplies it.
        if (!game || !game.Inventory)
            return;
        int index = 0;
        for (int i = 0; i < 5; i++)
        {
            // Hide unavailable weapons/recipes and compact the remaining icons to the left.
            bool available = game.CanEquip(i);
            equipment[i].gameObject.SetActive(available);
            if (!available)
                continue;
            equipment[i].anchoredPosition = new Vector2(16 + index++ * EquipmentSpacing, -16);

            // Only the equipped slot is fully opaque and shows its name.
            bool selected = game.SelectedItem == i;
            equipment[i].GetComponent<CanvasGroup>().alpha = selected ? 1 : 0.3f;
            equipmentNames[i].gameObject.SetActive(selected);
        }

        // Clear old resource visibility before rebuilding it from the nonempty inventory stacks.
        for (int i = 0; i < 2; i++)
            materials[i].gameObject.SetActive(false);

        // Oldest item stays at the right edge; a newly acquired type appears to its left.
        for (int i = 0; i < game.Inventory.AcquisitionOrder.Count; i++)
        {
            string item = game.Inventory.AcquisitionOrder[i];
            int slot = item == "Stick" ? 0 : 1;
            materials[slot].gameObject.SetActive(true);
            materials[slot].anchoredPosition = new Vector2(-16 - i * MaterialSpacing, 16);
            counts[slot].text = game.Inventory.Count(item).ToString();
        }
    }
}
