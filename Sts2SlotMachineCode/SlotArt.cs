using System;
using System.IO;
using Godot;

namespace Sts2SlotMachine;

/// <summary>Loads the loose slot-machine PNGs shipped next to the mod DLL (swappable without a rebuild).</summary>
internal static class SlotArt
{
    internal static Texture2D? LoadPng(string file)
    {
        try
        {
            string? dir = Path.GetDirectoryName(typeof(SlotArt).Assembly.Location);
            if (string.IsNullOrEmpty(dir)) return null;
            string path = Path.Combine(dir, file);
            if (!File.Exists(path)) return null;
            var img = Image.LoadFromFile(path);
            return img != null ? ImageTexture.CreateFromImage(img) : null;
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"[{MainFile.ModId}] load {file} failed: {e.Message}");
            return null;
        }
    }
}
