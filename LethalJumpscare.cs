using System;
using System.Collections;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using GameNetcodeStuff;
using HarmonyLib;
using LethalModUtils;
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

    public static int MinTimer => Instance.minTimer?.Value ?? MIN_TIMER;
    public static int MaxTimer => Instance.maxTimer?.Value ?? MAX_TIMER;

    private const string FLASHBANG_SOUND = "Sounds/flashbang.ogg";
    public static AudioClip? FlashbangSound { get; private set; }

    private const string JUMPSCARE_IMAGE = "Images/jumpscare.png";
    public static Texture2D? JumpscareImage { get; private set; }

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

        FlashbangSound = Audio.TryLoad(rel(FLASHBANG_SOUND), TimeSpan.FromSeconds(10));
        a();
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

        async void a() => JumpscareImage = await Image.TryLoadAsync(rel(JUMPSCARE_IMAGE));
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
        try
        {
            Logger.LogDebug(
                $">> Jumpscare() FlashbangSound:{FlashbangSound?.ToString() ?? "null"}"
            );
            var player = FlashbangSound?.Play();

            GameNetworkManager.Instance.localPlayerController.gameplayCamera.enabled = false;

            Logger.LogDebug($"   waiting... player:{player?.ToString() ?? "null"}");
            Graphics.Blit(
                JumpscareImage ?? Texture2D.whiteTexture,
                GameNetworkManager.Instance.localPlayerController.gameplayCamera.targetTexture
            );
            yield return player == null
                ? new WaitForSeconds(5)
                : new WaitUntil(() => player.State != Audio.AudioPlayer.PlayerState.Playing);

            Logger.LogDebug("<< Jumpscare");
            GameNetworkManager.Instance.localPlayerController.gameplayCamera.enabled = true;
            player?.Cancel();
        }
        finally
        {
            jumpscare = null;
        }
    }

    private static IEnumerator LoadImage(
        Uri uri,
        Action<Texture2D> success,
        Action<string>? error = null
    )
    {
        var path = Path.GetFullPath(uri.AbsolutePath);
        Logger.LogDebug($">> LoadImage({path}, {success}, {error})");

        var webRequest = UnityWebRequestTexture.GetTexture(path);
        yield return webRequest.SendWebRequest();

        if (webRequest.result != UnityWebRequest.Result.Success)
        {
            Logger.LogError($"Error loading {path}: {webRequest.error}");
            error?.Invoke(webRequest.error);
            yield break;
        }

        var texture2d = DownloadHandlerTexture.GetContent(webRequest);
        if (texture2d)
        {
            Logger.LogInfo($"Loaded {Path.GetFileName(path)}");
            success(texture2d);
            yield break;
        }

        Logger.LogWarning($"Error loading {path}");
        error?.Invoke(string.Empty);
    }

    private Uri rel(string path) =>
        new(Path.Combine(Path.GetDirectoryName(GetType().Assembly.Location) ?? string.Empty, path));
}
