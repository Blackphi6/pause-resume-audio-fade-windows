using System.Reflection;

// NAudio.Wasapi の中で DefaultDeviceChanged / DeviceStateChanged イベントを持つ公開型を探す(一時的な調査用)
internal static class Probe
{
    public static void Run()
    {
        var asm = typeof(NAudio.CoreAudioApi.MMDeviceEnumerator).Assembly;
        foreach (var t in asm.GetTypes())
        {
            foreach (var e in t.GetEvents(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                if (e.Name.Contains("DefaultDeviceChanged") || e.Name.Contains("DeviceStateChanged"))
                    Console.WriteLine($"{t.FullName} (public={t.IsPublic}) event {e.Name} : {e.EventHandlerType}");
            }
        }
        foreach (var name in new[] { "DefaultDeviceChangedEventArgs", "DeviceStateChangedEventArgs" })
        {
            var t = asm.GetTypes().FirstOrDefault(x => x.Name == name);
            if (t == null) continue;
            Console.WriteLine($"== {t.FullName} public={t.IsPublic}");
            foreach (var m in t.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)) Console.WriteLine($"   {m}");
        }
        foreach (var t in asm.GetTypes().Where(x => x.Name.Contains("MMDeviceNotification") || x.Name == "MMDeviceEnumerator"))
        {
            Console.WriteLine($"== {t.FullName} public={t.IsPublic}");
            foreach (var m in t.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                         .Where(m => m.MemberType is MemberTypes.Method or MemberTypes.Event))
                Console.WriteLine($"   {m}");
        }
    }
}
