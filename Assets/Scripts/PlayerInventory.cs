using System;
using System.Collections.Generic;
using System.Numerics;
using UnityEngine;

// Stores unlimited-size item counts and remembers the HUD order of crafting materials.
public sealed class PlayerInventory : MonoBehaviour
{
    // BigInteger avoids fixed integer limits; guns are stored but omitted from material order.
    readonly Dictionary<string, BigInteger> counts = new Dictionary<string, BigInteger>();
    readonly List<string> order = new List<string>();
    public IReadOnlyList<string> AcquisitionOrder => order;
    public event Action Changed;
    public bool HasGun => Count("Gun") > 0;
    public BigInteger Count(string item) => counts.TryGetValue(item, out var count) ? count : BigInteger.Zero;

    // Accept known item types and notify the equipment/HUD whenever ownership changes.
    public void Add(string item, int quantity)
    {
        // Zero/negative additions are not pickups and must not create empty slots.
        if (quantity <= 0)
            return;

        // Weapons grant ownership without appearing in the bottom-right crafting list.
        if (item == "Gun")
        {
            counts[item] = Count(item) + quantity;
            Changed?.Invoke();
            return;
        }

        // Only sticks and bulbs are crafting resources; first acquisition appends their HUD order.
        if (item != "Stick" && item != "Bulb")
            return;
        if (Count(item).IsZero)
            order.Add(item);
        counts[item] = Count(item) + quantity;
        Changed?.Invoke();
    }

    // Both lamp recipes use equal amounts of sticks and bulbs.
    public bool CanCraft(int amount) => Count("Stick") >= amount && Count("Bulb") >= amount;

    // Validate the whole recipe before subtracting anything, then remove empty HUD entries.
    public bool SpendRecipe(int amount)
    {
        // Failed recipes leave both resource counts untouched.
        if (amount <= 0 || !CanCraft(amount))
            return false;
        counts["Stick"] -= amount;
        counts["Bulb"] -= amount;
        order.RemoveAll(item => Count(item).IsZero);
        Changed?.Invoke();
        return true;
    }

    // A new life starts with no resources, no weapons, and no remembered item order.
    public void Clear()
    {
        counts.Clear();
        order.Clear();
        Changed?.Invoke();
    }
}
