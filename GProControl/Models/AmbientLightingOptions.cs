namespace GProControl.Models;

public sealed record AmbientLightingOptions(int AudioSource = 0, int AudioStyle = 0, int AudioPalette = 0, int ScreenSamples = 4)
{
    public bool IsValid => AudioSource is 0 or 1 && AudioStyle is 0 or 1 && AudioPalette is >=0 and <=3 && ScreenSamples is >=1 and <=5;
}
