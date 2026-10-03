using System.Text.Json.Serialization;

namespace BPTrainer;

public class HeroData
{
    [JsonPropertyName("heroId")]
    public string HeroId { get; set; } = "";

    [JsonPropertyName("heroName")]
    public string HeroName { get; set; } = "";

    [JsonPropertyName("imagePath")]
    public string ImagePath { get; set; } = "";

    [JsonPropertyName("row")]
    public int Row { get; set; } = 0;

    public HeroData() { }

    public HeroData(string id, string name, string imagePath, int row = 0)
    {
        HeroId = id;
        HeroName = name;
        ImagePath = imagePath;
        Row = row;
    }
}
