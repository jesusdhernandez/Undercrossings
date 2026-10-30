using UnityEngine;
using UnityEditor;
using System.IO;
using UnityEngine.UIElements;

// Unity's template readme inspector, retained as editor-only project documentation.
[CustomEditor(typeof(Readme))]
[InitializeOnLoad]
sealed class ReadmeEditor : Editor
{
    const string k_ShowedReadmeSessionStateName = "ReadmeEditor.showedReadme";
    const string k_ReadmeSourceDirectory = "Assets/TutorialInfo";

    // Delay selection until the editor is ready to load assets and window layouts.
    static ReadmeEditor() => EditorApplication.delayCall += SelectReadmeAutomatically;

    static void SelectReadmeAutomatically()
    {
        // Show the readme at most once per editor session, not after every script reload.
        if (!SessionState.GetBool(k_ShowedReadmeSessionStateName, false))
        {
            var readme = SelectReadme();
            SessionState.SetBool(k_ShowedReadmeSessionStateName, true);

            // Only restore the welcome layout the first time this particular readme is loaded.
            if (readme && !readme.loadedLayout)
            {
                EditorUtility.LoadWindowLayout(Path.Combine(Application.dataPath, "TutorialInfo/Layout.wlt"));
                readme.loadedLayout = true;
            }
        }
    }

    // Find the single template readme asset and select it so this inspector is displayed.
    static Readme SelectReadme()
    {
        var ids = AssetDatabase.FindAssets("Readme t:Readme");

        // Refuse an ambiguous/missing asset rather than selecting an arbitrary readme.
        if (ids.Length != 1)
        {
            Debug.Log("Couldn't find a readme");
            return null;
        }

        var readmeObject = AssetDatabase.LoadMainAssetAtPath(AssetDatabase.GUIDToAssetPath(ids[0]));
        Selection.objects = new UnityEngine.Object[] { readmeObject };
        return (Readme)readmeObject;
    }

    // This optional editor button removes only template documentation after explicit confirmation.
    void RemoveTutorial()
    {
        // Cancel leaves all assets untouched; Proceed enables the template's cleanup routine.
        if (EditorUtility.DisplayDialog(
                "Remove Readme Assets",

                $"All contents under {k_ReadmeSourceDirectory} will be removed, are you sure you want to proceed?",
                "Proceed", "Cancel"))
        {
            // Remove the known tutorial folder and metadata only if that folder still exists.
            if (Directory.Exists(k_ReadmeSourceDirectory))
            {
                FileUtil.DeleteFileOrDirectory(k_ReadmeSourceDirectory);
                FileUtil.DeleteFileOrDirectory(k_ReadmeSourceDirectory + ".meta");
            }
            else
            {
                Debug.Log($"Could not find the Readme folder at {k_ReadmeSourceDirectory}");
            }

            var readmeAsset = SelectReadme();

            // The readme asset may live outside the tutorial folder; remove that reference as well.
            if (readmeAsset != null)
            {
                var path = AssetDatabase.GetAssetPath(readmeAsset);
                FileUtil.DeleteFileOrDirectory(path + ".meta");
                FileUtil.DeleteFileOrDirectory(path);
            }

            AssetDatabase.Refresh();
        }
    }

    // Suppress Unity's default IMGUI header/body because the UI Toolkit tree below replaces them.
    protected sealed override void OnHeaderGUI() {}
    public sealed override void OnInspectorGUI() {}

    // Build the styled readme UI from the serialized sections without affecting gameplay canvases.
    public override VisualElement CreateInspectorGUI()
    {
        var readme = (Readme)target;

        VisualElement root = new();
        root.styleSheets.Add(readme.commonStyle);
        root.styleSheets.Add(EditorGUIUtility.isProSkin ? readme.darkStyle : readme.lightStyle);

        // Return the element after styling so callers can build compact nested UI expressions.
        VisualElement ChainWithClass(VisualElement created, string className)
        {
            created.AddToClassList(className);
            return created;
        }

        // Combine the authored icon and title into the inspector header.
        VisualElement title = new();
        title.AddToClassList("title");
        title.Add(ChainWithClass(new Image() { image = readme.icon }, "title__icon"));
        title.Add(ChainWithClass(new Label(readme.title), "title__text"));
        root.Add(title);

        // Create only the nonempty content pieces for each readme section.
        foreach (var section in readme.sections)
        {
            VisualElement part = new();
            part.AddToClassList("section");

            if (!string.IsNullOrEmpty(section.heading))
                part.Add(ChainWithClass(new Label(section.heading), "section__header"));

            if (!string.IsNullOrEmpty(section.text))
                part.Add(ChainWithClass(new Label(section.text), "section__body"));

            // Link labels open their section's URL only after the user clicks them.
            if (!string.IsNullOrEmpty(section.linkText))
            {
                var link = ChainWithClass(new Label(section.linkText), "section__link");
                link.RegisterCallback<ClickEvent>(evt => Application.OpenURL(section.url));
                part.Add(link);
            }

            root.Add(part);
        }

        // Expose the optional template-cleanup action after the informational content.
        var button = new Button(RemoveTutorial) { text = "Remove Readme Assets" };
        button.AddToClassList("remove-readme-button");
        root.Add(button);

        return root;
    }
}
