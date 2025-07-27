using System;
using System.Collections;
using System.IO;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using GameNetcodeStuff;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Networking;
using Random = System.Random;

namespace LethalJumpscare;

[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
public class LethalJumpscare : BaseUnityPlugin
{
    public static LethalJumpscare Instance { get; private set; } = null!;
    internal static new ManualLogSource Logger { get; private set; } = null!;
    internal static Harmony? Harmony { get; set; }

    private const int MIN_TIMER = 60;
    private const int MAX_TIMER = 7200;

    private ConfigEntry<int>? minTimer;
    private ConfigEntry<int>? maxTimer;

    public static int MinTimer => Instance?.minTimer?.Value ?? MIN_TIMER;
    public static int MaxTimer => Instance?.maxTimer?.Value ?? MAX_TIMER;

    private const string FLASHBANG_SOUND = "Sounds/flashbang.ogg";
    public static AudioClip? FlashbangSound { get; private set; }

    private const string JUMPSCARE_IMAGE = "Images/jumpscare.png";
    public static Texture2D JumpscareImage { get; private set; } = Texture2D.whiteTexture;

    private void Awake()
    {
        Logger = base.Logger;
        Instance = this;

        minTimer = Config.Bind(
            "General",
            "MinTimer",
            MIN_TIMER,
            "What the minimum waiting time should be for a jumpscare (after landing on a moon)"
        );
        maxTimer = Config.Bind(
            "General",
            "MaxTimer",
            MAX_TIMER,
            "What the maximum waiting time should be for a jumpscare (after landing on a moon)"
        );

        StartCoroutine(LoadSound(rel(FLASHBANG_SOUND), clip => FlashbangSound = clip));
        StartCoroutine(LoadImage(rel(JUMPSCARE_IMAGE), texture2d => JumpscareImage = texture2d));
        Patch();

        Logger.LogInfo($"{MyPluginInfo.PLUGIN_GUID} v{MyPluginInfo.PLUGIN_VERSION} has loaded!");
        return;

        void Patch()
        {
            Harmony ??= new Harmony(MyPluginInfo.PLUGIN_GUID);
            Logger.LogDebug("Patching...");
            Harmony.PatchAll();
            Logger.LogDebug("Finished patching!");
        }
    }

    private static Random timerRandom = null!;
    public static float Timer { get; private set; } = -1f;

    [HarmonyPatch(typeof(StartOfRound), nameof(StartOfRound.openingDoorsSequence))]
    internal static class OpeningDoorsSequencePatch
    {
        // ReSharper disable once UnusedMember.Local
        private static void Postfix(ref StartOfRound __instance)
        {
            timerRandom = new Random(__instance.randomMapSeed);
            Timer = timerRandom.Next(MinTimer, MaxTimer);
            Logger.LogDebug(
                $"<< OpeningDoorsSequencePatch({__instance}) seed:{__instance.randomMapSeed} Timer:{Timer}"
            );
        }
    }

    [HarmonyPatch(typeof(StartOfRound), nameof(StartOfRound.Update))]
    internal static class UpdatePatch
    {
        // ReSharper disable once UnusedMember.Local
        private static void Postfix(ref StartOfRound __instance)
        {
            Logger.LogDebug(
                $">> UpdatePatch({__instance}) inShipPhase:{__instance.inShipPhase} localPlayerController:{b(__instance.localPlayerController)} firingPlayersCutsceneRunning:{__instance.firingPlayersCutsceneRunning} Timer:{Timer}"
            );
            if (
                __instance.inShipPhase
                || __instance.localPlayerController == null
                || __instance.localPlayerController.isPlayerDead
                || __instance.localPlayerController.inSpecialInteractAnimation
                || __instance.firingPlayersCutsceneRunning
                || jumpscare != null
            )
                return;

            if (Timer < 0)
                a(ref __instance);
            else
                Timer -= Time.deltaTime;

            return;

            string b(PlayerControllerB? player) =>
                player == null
                    ? "null"
                    : $"{{ isPlayerDead:{player.isPlayerDead}, inSpecialInteractAnimation:{player.inSpecialInteractAnimation} }}";
        }

        private static Random walkingRandom = new();

        private static void a(ref StartOfRound __instance)
        {
            Logger.LogDebug(
                $">> a({__instance}) isWalking:{__instance.localPlayerController.isWalking}"
            );
            if (!__instance.localPlayerController.isWalking)
            {
                Timer = (float)walkingRandom.NextDouble() * 2f;
                return;
            }

            Timer = timerRandom.Next(MinTimer, MaxTimer);

            jumpscare = __instance.localPlayerController.StartCoroutine(Jumpscare());
        }
    }

    private static Coroutine? jumpscare;

    private static IEnumerator Jumpscare()
    {
        Logger.LogDebug($">> Jumpscare() FlashbangSound:{a(FlashbangSound)}");
        if (FlashbangSound is null)
            goto end;

        var audioSourceObject = new GameObject();
        var audioSource = audioSourceObject.AddComponent<AudioSource>();
        audioSourceObject.transform.position = Vector3.zero;
        audioSource.PlayOneShot(FlashbangSound);

        GameNetworkManager.Instance.localPlayerController.gameplayCamera.enabled = false;
        while (audioSource && audioSource.isPlaying)
        {
            Logger.LogDebug(
                $"Sound playing, waiting... {GameNetworkManager.Instance.localPlayerController.gameplayCamera.enabled}"
            );
            Graphics.Blit(
                JumpscareImage,
                GameNetworkManager.Instance.localPlayerController.gameplayCamera.targetTexture
            );
            yield return null;
        }
        GameNetworkManager.Instance.localPlayerController.gameplayCamera.enabled = true;

        while (audioSource && audioSource.isPlaying)
            yield return null;
        if (audioSourceObject)
            Destroy(audioSourceObject);

        end:
        jumpscare = null;
        yield break;

        string a(AudioClip? audioClip) => audioClip == null ? "null" : audioClip.ToString();
    }

    private static IEnumerator LoadSound(string path, Action<AudioClip> callback)
    {
        var audioType = Path.GetExtension(path).ToLower() switch
        {
            ".ogg" => AudioType.OGGVORBIS,
            ".mp3" => AudioType.MPEG,
            ".wav" => AudioType.WAV,
            ".m4a" => AudioType.ACC,
            ".aiff" => AudioType.AIFF,
            _ => AudioType.UNKNOWN,
        };
        Logger.LogDebug($">> LoadSound({Path.GetFullPath(path)}) audioType:{audioType}");
        if (audioType == AudioType.UNKNOWN)
        {
            Logger.LogError($"Error loading {Path.GetFullPath(path)}: Unknown file type");
            yield break;
        }

        var webRequest = UnityWebRequestMultimedia.GetAudioClip(path, audioType);
        yield return webRequest.SendWebRequest();

        if (webRequest.result != UnityWebRequest.Result.Success)
        {
            Logger.LogError($"Error loading {Path.GetFullPath(path)}: {webRequest.error}");
            yield break;
        }

        var audioClip = DownloadHandlerAudioClip.GetContent(webRequest);
        if (audioClip && audioClip.loadState == AudioDataLoadState.Loaded)
        {
            Logger.LogInfo($"Loaded {Path.GetFileName(path)}");
            callback(audioClip);
            yield break;
        }

        Logger.LogWarning($"Error loading {Path.GetFullPath(path)}: {audioClip.loadState}");
    }

    private static IEnumerator LoadImage(string path, Action<Texture2D> callback)
    {
        Logger.LogDebug($">> LoadImage({Path.GetFullPath(path)}, {callback})");

        var webRequest = UnityWebRequestTexture.GetTexture(path);
        yield return webRequest.SendWebRequest();

        if (webRequest.result != UnityWebRequest.Result.Success)
        {
            Logger.LogError($"Error loading {Path.GetFullPath(path)}: {webRequest.error}");
            yield break;
        }

        var texture2d = DownloadHandlerTexture.GetContent(webRequest);
        if (texture2d)
        {
            Logger.LogInfo($"Loaded {Path.GetFileName(path)}");
            callback(texture2d);
            yield break;
        }

        Logger.LogWarning($"Error loading {Path.GetFullPath(path)}");
    }

    private string rel(string path) =>
        Path.Combine(Path.GetDirectoryName(GetType().Assembly.Location) ?? string.Empty, path);
}
