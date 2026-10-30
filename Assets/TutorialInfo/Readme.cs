using System;
using UnityEngine;
using UnityEngine.UIElements;

// Unity template readme data; this editor-only documentation is unrelated to gameplay systems.
public class Readme : ScriptableObject
{
    // The custom inspector uses these assets to display the template's introductory content.
    public StyleSheet commonStyle;
    public StyleSheet darkStyle;
    public StyleSheet lightStyle;
    public Texture2D icon;
    public string title;
    public Section[] sections;
    public bool loadedLayout;

    // Each serialized section can contain a heading, body text, and an optional web link.
    [Serializable]
    public class Section
    {
        public string heading, text, linkText, url;
    }
}
