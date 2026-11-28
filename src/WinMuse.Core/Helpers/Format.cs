namespace WinMuse.Helpers;

public static class Format
{
    public static string Time(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
    }

    public static string Time(double seconds) => Time(TimeSpan.FromSeconds(double.IsFinite(seconds) ? seconds : 0));

    public static string LongTime(TimeSpan t)
    {
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours} hr {t.Minutes} min";
        if (t.TotalMinutes >= 1) return $"{(int)t.TotalMinutes} min";
        return $"{t.Seconds} sec";
    }

    public static string Size(long bytes)
    {
        string[] u = { "B", "KB", "MB", "GB" };
        double v = bytes; int i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return $"{v:0.#} {u[i]}";
    }

    /// <summary>"1 song", "2 songs", "1,234 songs".</summary>
    public static string Count(int n, string one, string many) => $"{n:N0} {(n == 1 ? one : many)}";

    public static string Greeting() => Greeting(DateTime.Now.Hour);

    public static string Greeting(int hour) => hour < 5 ? "Good Evening" : hour < 12 ? "Good Morning" : hour < 17 ? "Good Afternoon" : "Good Evening";

    public static long NowUnix() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
}
