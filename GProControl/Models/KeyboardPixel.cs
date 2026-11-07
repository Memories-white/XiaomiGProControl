namespace GProControl.Models;

/// <summary>A B21 matrix location with full-range (0–255) software RGB values.</summary>
public readonly record struct KeyboardPixel(byte Index, byte Red, byte Green, byte Blue);
