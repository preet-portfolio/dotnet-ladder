namespace Ladder.Json.Bench;

internal static class Corpus
{
    public static readonly string[] Files = ["twitter.json", "citm_catalog.json", "canada.json"];

    /// <summary>Finds judge/vendor/corpus by walking up from the binary; run 01-json/judge/run-judge.sh first to download it.</summary>
    public static byte[] Load(string name)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "01-json", "judge", "vendor", "corpus", name);
            if (File.Exists(candidate))
            {
                return File.ReadAllBytes(candidate);
            }
        }

        throw new FileNotFoundException($"Corpus file {name} not found; run 01-json/judge/run-judge.sh first.");
    }
}
