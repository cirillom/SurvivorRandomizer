using BepInEx;
using RoR2;
using RoR2.UI;
using System.Collections;
using System.Linq;
using System.Security;
using System.Security.Permissions;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

#pragma warning disable CS0618
[assembly: SecurityPermission(SecurityAction.RequestMinimum, SkipVerification = true)]
#pragma warning restore CS0618

namespace SurvivorRandomizer;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public sealed class Plugin : BaseUnityPlugin
{
    public const string PluginGuid = "com.cirillom.survivorrandomizer";
    public const string PluginName = "Survivor Randomizer";
    public const string PluginVersion = "1.1.1";

    private CharacterSelectController? _characterSelectController;

    private void Awake()
    {
        Logger.LogInfo("Survivor Randomizer loaded!");

        On.RoR2.UI.CharacterSelectController.Awake += CharacterSelectControllerAwake;
    }

    private void CharacterSelectControllerAwake(
        On.RoR2.UI.CharacterSelectController.orig_Awake orig,
        CharacterSelectController self)
    {
        orig(self);

        _characterSelectController = self;

        StartCoroutine(CreateButtons(self));
    }

    private IEnumerator CreateButtons(CharacterSelectController controller)
    {
        yield return null;
        yield return null;

        if (!controller || controller.readyButton == null)
            yield break;

        var readyPanel = controller.readyButton.transform.parent as RectTransform;

        if (readyPanel == null)
        {
            Logger.LogWarning("Could not find ReadyPanel.");
            yield break;
        }

        // IMPORTANT: sibling of ReadyPanel, not child of it.
        // This keeps us completely outside ReadyPanel's layout system.
        var rootObject = new GameObject("SurvivorRandomizerPanel", typeof(RectTransform));
        rootObject.layer = readyPanel.gameObject.layer;
        rootObject.transform.SetParent(readyPanel.parent, false);

        var root = rootObject.GetComponent<RectTransform>();

        // Use the same anchors/pivot as ReadyPanel so we stay attached to it.
        root.anchorMin = readyPanel.anchorMin;
        root.anchorMax = readyPanel.anchorMax;
        root.pivot = readyPanel.pivot;

        // Position our panel above ReadyPanel.
        root.anchoredPosition = readyPanel.anchoredPosition + new Vector2(0f, 85f);
        root.sizeDelta = new Vector2(500f, 54f);
        root.localScale = Vector3.one;

        if (IsEclipseRun())
        {
            CreateLobbyButton(controller, root, "SurvivorRandomizerRandom", "RANDOM SURVIVOR", new Vector2(-120f, 0f), new Color(0.10f, 0.30f, 0.48f, 1f), RandomizeSurvivor);

            CreateLobbyButton(controller, root, "SurvivorRandomizerLowest", "LOWEST ECLIPSE", new Vector2(120f, 0f), new Color(0.48f, 0.30f, 0.08f, 1f), RandomizeLowestEclipse);
        }
        else
        {
            CreateLobbyButton(controller, root, "SurvivorRandomizerRandom", "RANDOM SURVIVOR", Vector2.zero, new Color(0.10f, 0.30f, 0.48f, 1f), RandomizeSurvivor);
        }

        Logger.LogInfo("Created survivor randomizer panel.");
    }

    private void CreateLobbyButton(CharacterSelectController controller, RectTransform parent, string objectName, string text, Vector2 position, Color normalColor, UnityEngine.Events.UnityAction action)
    {
        var template = controller.readyButton;
        var templateImage = template.GetComponent<Image>();
        var templateLabel = template.GetComponentInChildren<TextMeshProUGUI>(true);

        var buttonObject = new GameObject(objectName, typeof(RectTransform), typeof(Image));
        buttonObject.layer = template.gameObject.layer;
        buttonObject.transform.SetParent(parent, false);

        var rect = buttonObject.GetComponent<RectTransform>();
        rect.anchorMin = new Vector2(0.5f, 0.5f);
        rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.anchoredPosition = position;
        rect.sizeDelta = new Vector2(225f, 50f);
        rect.localScale = Vector3.one;

        var image = buttonObject.GetComponent<Image>();
        image.sprite = templateImage.sprite;
        image.type = templateImage.type;

        buttonObject.AddComponent<MPEventSystemLocator>();

        var button = buttonObject.AddComponent<MPButton>();
        button.targetGraphic = image;
        button.transition = template.transition;
        button.navigation = new Navigation { mode = Navigation.Mode.None };
        button.interactable = true;
        button.onClick.AddListener(action);

        var colors = template.colors;
        colors.normalColor = normalColor;
        colors.highlightedColor = Color.Lerp(normalColor, Color.white, 0.30f);
        colors.selectedColor = colors.highlightedColor;
        colors.pressedColor = Color.Lerp(normalColor, Color.black, 0.25f);
        colors.disabledColor = new Color(normalColor.r, normalColor.g, normalColor.b, 0.35f);
        button.colors = colors;

        var labelObject = new GameObject("Label", typeof(RectTransform), typeof(TextMeshProUGUI));
        labelObject.layer = buttonObject.layer;
        labelObject.transform.SetParent(buttonObject.transform, false);

        var label = labelObject.GetComponent<TextMeshProUGUI>();

        if (templateLabel != null)
        {
            label.font = templateLabel.font;
            label.fontSharedMaterial = templateLabel.fontSharedMaterial;
            label.fontStyle = templateLabel.fontStyle;
        }

        label.text = text;
        label.color = Color.white;
        label.alignment = TextAlignmentOptions.Center;
        label.enableAutoSizing = true;
        label.fontSizeMin = 14f;
        label.fontSizeMax = 22f;
        label.raycastTarget = false;

        var labelRect = label.rectTransform;
        labelRect.anchorMin = Vector2.zero;
        labelRect.anchorMax = Vector2.one;
        labelRect.offsetMin = new Vector2(10f, 4f);
        labelRect.offsetMax = new Vector2(-10f, -4f);
    }

    private void RandomizeSurvivor()
    {
        var survivors = GetEligibleSurvivors();

        if (survivors.Length == 0)
        {
            Logger.LogWarning("No eligible survivors found.");
            return;
        }

        var survivor =
            survivors[UnityEngine.Random.Range(0, survivors.Length)];

        SelectSurvivor(survivor);

        Logger.LogInfo(
            IsEclipseRun()
                ? $"Random survivor: {survivor.cachedName} — Eclipse {GetNextEclipseLevel(survivor)}"
                : $"Random survivor: {survivor.cachedName}"
        );
    }

    private void RandomizeLowestEclipse()
    {
        if (!IsEclipseRun())
            return;

        var localUser = LocalUserManager.GetFirstLocalUser();

        if (localUser == null)
        {
            Logger.LogWarning("Could not find local user.");
            return;
        }

        var survivors = GetEligibleSurvivors();

        var unfinished = survivors
            .Select(survivor => new
            {
                Survivor = survivor,
                CompletedLevel =
                    EclipseRun.GetLocalUserSurvivorCompletedEclipseLevel(
                        localUser,
                        survivor
                    )
            })
            .Where(entry => entry.CompletedLevel < 8)
            .ToArray();

        if (unfinished.Length == 0)
        {
            Logger.LogInfo("Eclipse 8 completed on every available survivor.");
            return;
        }

        var lowestNextLevel =
            unfinished.Min(entry => entry.CompletedLevel + 1);

        var candidates = unfinished
            .Where(entry =>
                entry.CompletedLevel + 1 == lowestNextLevel
            )
            .Select(entry => entry.Survivor)
            .ToArray();

        var survivor =
            candidates[UnityEngine.Random.Range(0, candidates.Length)];

        SelectSurvivor(survivor);

        Logger.LogInfo(
            $"Lowest Eclipse: selected {survivor.cachedName} at E{lowestNextLevel} " +
            $"from {candidates.Length} candidate(s)."
        );
    }

    private SurvivorDef[] GetEligibleSurvivors()
    {
        var localUser = LocalUserManager.GetFirstLocalUser();

        if (localUser == null)
            return [];

        return SurvivorCatalog.orderedSurvivorDefs
            .Where(survivor =>
                survivor != null &&
                !survivor.hidden &&
                SurvivorCatalog.SurvivorIsUnlockedOnThisClient(
                    survivor.survivorIndex
                ) &&
                survivor.CheckRequiredExpansionEnabled() &&
                survivor.CheckUserHasRequiredEntitlement(localUser))
            .ToArray();
    }

    private int GetNextEclipseLevel(SurvivorDef survivor)
    {
        var localUser = LocalUserManager.GetFirstLocalUser();

        if (localUser == null)
            return 1;

        var completed =
            EclipseRun.GetLocalUserSurvivorCompletedEclipseLevel(
                localUser,
                survivor
            );

        return Mathf.Clamp(completed + 1, 1, 8);
    }

    private void SelectSurvivor(SurvivorDef survivor)
    {
        if (!_characterSelectController)
            return;

        if (!PreGameController.instance)
            return;

        if (!PreGameController.instance.IsCharacterSwitchingCurrentlyAllowed())
            return;

        var characterSelectBar =
            _characterSelectController
                .GetComponentInChildren<CharacterSelectBarController>();

        if (!characterSelectBar)
        {
            Logger.LogWarning(
                "Could not find CharacterSelectBarController."
            );

            return;
        }

        characterSelectBar.PickIconBySurvivorDef(survivor);

        _characterSelectController
            .SetSurvivorInfoPanelActive(true);
    }

    private static bool IsEclipseRun()
    {
        return PreGameController.instance &&
               PreGameController.instance.gameModeIndex ==
               GameModeCatalog.FindGameModeIndex("EclipseRun");
    }
}
