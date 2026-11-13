using Meitou.Transport.Verification;
using HarmonyLib;
using Meitou.Modding;

// The .NET test process loads this entry from outside the mods directory.
// The normal game loader discovers the framework and the scenario's content packs.
internal static class StartupHook
{
    public static void Initialize()
    {
        if (Environment.GetEnvironmentVariable("MEITOU_TRANSPORT_TEST_CPU_VULKAN") == "1"
            && Environment.GetCommandLineArgs().Contains("--headless"))
        {
            // A software trial must never fall back to the desktop GPU. The engine has chosen its
            // physical adapter at CreateLogicalDevice, but has not created a device or submitted work.
            var assembly = typeof(IMod).Assembly;
            var graphics = assembly.GetType("Meitou.Game.Rendering.Gpu.Core.VulkanDevice", true)!;
            var display = assembly.GetType("Meitou.Game.Rendering.Display.VulkanDisplay", true)!;
            var harmony = new Harmony("meitou.transport.verification.cpu-vulkan");
            harmony.Patch(AccessTools.Method(display, "LoadDlss"),
                prefix: new HarmonyMethod(typeof(StartupHook), nameof(SkipVendorUpscaler)));
            harmony.Patch(AccessTools.Method(graphics, "CreateLogicalDevice"),
                prefix: new HarmonyMethod(typeof(StartupHook), nameof(RequireCpu)));
        }
        LiveRide.Arm();
        LiveVoyage.Arm();
    }

    private static void SkipVendorUpscaler(ref bool dlss) => dlss = false;

    private static void RequireCpu(object __instance)
    {
        var properties = AccessTools.Property(__instance.GetType(), "Properties").GetValue(__instance)!;
        var kind = AccessTools.Field(properties.GetType(), "DeviceType").GetValue(properties);
        if (Convert.ToInt32(kind) != 4) // VK_PHYSICAL_DEVICE_TYPE_CPU
            throw new InvalidOperationException($"CPU-only playtest selected a {kind} device.");
        Console.WriteLine("QA CPU renderer " + AccessTools.Property(__instance.GetType(), "DeviceName").GetValue(__instance));
    }
}
