namespace GW2RaidStats.Core.Configuration;

public class RecapConfig
{
    public bool Enabled { get; set; } = true;
    public int Year { get; set; } = DateTime.Now.Year;
    public bool Published { get; set; }
}
