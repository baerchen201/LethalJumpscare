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

    public static int MinTimer => Instance.minTimer?.Value ?? MIN_TIMER;
    public static int MaxTimer => Instance.maxTimer?.Value ?? MAX_TIMER;

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
    private static readonly int MainTex = Shader.PropertyToID("_MainTex");

    private static IEnumerator Jumpscare()
    {
        Logger.LogDebug($">> Jumpscare() FlashbangSound:{a(FlashbangSound)}");
        if (!FlashbangSound)
            goto end;

        var shader = Shader.Find("HDRP/Unlit");
        if (!shader)
        {
            Logger.LogWarning("Shader not found, using fallback");

            var audioSourceObjectFb = new GameObject();
            var audioSourceFb = audioSourceObjectFb.AddComponent<AudioSource>();
            audioSourceObjectFb.transform.position = Vector3.zero;
            audioSourceFb.PlayOneShot(FlashbangSound);

            GameNetworkManager.Instance.localPlayerController.gameplayCamera.enabled = false;
            do
            {
                Logger.LogDebug(
                    $"Sound playing, waiting... {GameNetworkManager.Instance.localPlayerController.gameplayCamera.enabled}"
                );
                Graphics.Blit(
                    JumpscareImage,
                    GameNetworkManager.Instance.localPlayerController.gameplayCamera.targetTexture
                );
                yield return null;
            } while (audioSourceFb && audioSourceFb.isPlaying);
            GameNetworkManager.Instance.localPlayerController.gameplayCamera.enabled = true;

            if (audioSourceObjectFb)
                Destroy(audioSourceObjectFb);

            goto end;
        }

        var material = new Material(shader);
        material.SetTexture(MainTex, JumpscareImage);
        material.color = new Color(255f, 255f, 255f, 0.2f);

        var audioSourceObject = new GameObject();
        var audioSource = audioSourceObject.AddComponent<AudioSource>();
        audioSourceObject.transform.position = Vector3.zero;
        audioSource.PlayOneShot(FlashbangSound);

        GameNetworkManager.Instance.localPlayerController.gameplayCamera.enabled = false;
        do
        {
            Logger.LogDebug(
                $"Sound playing, waiting... {GameNetworkManager.Instance.localPlayerController.gameplayCamera.enabled}"
            );
            Graphics.Blit(
                null,
                GameNetworkManager.Instance.localPlayerController.gameplayCamera.targetTexture,
                material
            );
            yield return null;
        } while (audioSource && audioSource.isPlaying);
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

    private static IEnumerator LoadSound(
        string path,
        Action<AudioClip> success,
        Action<string>? error = null
    )
    {
        const string ERR_UNKNOWN_TYPE = "Unknown file type";

        var audioType = Path.GetExtension(path).ToLower() switch
        {
            ".ogg" => AudioType.OGGVORBIS,
            ".mp3" => AudioType.MPEG,
            ".wav" => AudioType.WAV,
            ".m4a" => AudioType.ACC,
            ".aiff" => AudioType.AIFF,
            _ => AudioType.UNKNOWN,
        };
        Logger.LogDebug(
            $">> LoadSound({Path.GetFullPath(path)}, {success}, {error}) audioType:{audioType}"
        );
        if (audioType == AudioType.UNKNOWN)
        {
            Logger.LogError($"Error loading {Path.GetFullPath(path)}: {ERR_UNKNOWN_TYPE}");
            error?.Invoke(ERR_UNKNOWN_TYPE);
            yield break;
        }

        var webRequest = UnityWebRequestMultimedia.GetAudioClip(path, audioType);
        yield return webRequest.SendWebRequest();

        if (webRequest.result != UnityWebRequest.Result.Success)
        {
            Logger.LogError($"Error loading {Path.GetFullPath(path)}: {webRequest.error}");
            error?.Invoke(webRequest.error);
            yield break;
        }

        var audioClip = DownloadHandlerAudioClip.GetContent(webRequest);
        if (audioClip && audioClip.loadState == AudioDataLoadState.Loaded)
        {
            Logger.LogInfo($"Loaded {Path.GetFileName(path)}");
            success(audioClip);
            yield break;
        }

        Logger.LogWarning($"Error loading {Path.GetFullPath(path)}: {audioClip.loadState}");
        error?.Invoke(audioClip.loadState.ToString());
    }

    private static IEnumerator LoadImage(
        string path,
        Action<Texture2D> success,
        Action<string>? error = null
    )
    {
        Logger.LogDebug($">> LoadImage({Path.GetFullPath(path)}, {success}, {error})");

        var webRequest = UnityWebRequestTexture.GetTexture(path);
        yield return webRequest.SendWebRequest();

        if (webRequest.result != UnityWebRequest.Result.Success)
        {
            Logger.LogError($"Error loading {Path.GetFullPath(path)}: {webRequest.error}");
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

        Logger.LogWarning($"Error loading {Path.GetFullPath(path)}");
        error?.Invoke(string.Empty);
    }

    private string rel(string path) =>
        Path.Combine(Path.GetDirectoryName(GetType().Assembly.Location) ?? string.Empty, path);
}

static class MaterialExtension
{
    private static readonly int SrcBlend = Shader.PropertyToID("_SrcBlend");
    private static readonly int DstBlend = Shader.PropertyToID("_DstBlend");
    private static readonly int ZWrite = Shader.PropertyToID("_ZWrite");

    internal static void Transparent(this Material material)
    {
        material.SetOverrideTag("RenderType", "Transparent");
        material.SetInt(SrcBlend, (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
        material.SetInt(DstBlend, (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        material.SetInt(ZWrite, 0);
        material.DisableKeyword("_ALPHATEST_ON");
        material.EnableKeyword("_ALPHABLEND_ON");
        material.DisableKeyword("_ALPHAPREMULTIPLY_ON");
        material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
    }
}
