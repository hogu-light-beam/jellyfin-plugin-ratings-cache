using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.RatingsCache.Services;

/// <summary>Journal du plugin : tampon en mémoire (page d'admin) + fichier + journal Jellyfin.</summary>
public sealed class PluginLog
{
    private const int MaxLines = 1500;
    private const long MaxFileBytes = 1024 * 1024;

    private readonly ILogger<PluginLog> _logger;
    private readonly object _lock = new();
    private readonly LinkedList<string> _lines = new();
    private readonly string _file;

    public PluginLog(ILogger<PluginLog> logger, IApplicationPaths paths)
    {
        _logger = logger;
        var dir = StorePaths.Dir(paths);
        Directory.CreateDirectory(dir);
        _file = Path.Combine(dir, "plugin.log");
        try
        {
            if (File.Exists(_file))
            {
                foreach (var l in File.ReadLines(_file).TakeLast(400))
                {
                    _lines.AddLast(l);
                }
            }
        }
        catch (IOException)
        {
            // journal illisible : on repart de zéro
        }
    }

    public void Info(string message) => Write("INFO", message, null);

    public void Warn(string message) => Write("WARN", message, null);

    public void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

    public IReadOnlyList<string> Tail(int count)
    {
        lock (_lock)
        {
            return _lines.TakeLast(Math.Max(1, Math.Min(count, MaxLines))).ToList();
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _lines.Clear();
            try
            {
                File.WriteAllText(_file, string.Empty);
            }
            catch (IOException)
            {
                // ignoré
            }
        }
    }

    private void Write(string level, string message, Exception? ex)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}" + (ex != null ? " : " + ex.Message : string.Empty);
        lock (_lock)
        {
            _lines.AddLast(line);
            while (_lines.Count > MaxLines)
            {
                _lines.RemoveFirst();
            }

            try
            {
                if (File.Exists(_file) && new FileInfo(_file).Length > MaxFileBytes)
                {
                    File.WriteAllLines(_file, _lines.TakeLast(400));
                }

                File.AppendAllText(_file, line + Environment.NewLine);
            }
            catch (IOException)
            {
                // ignoré
            }
        }

        switch (level)
        {
            case "ERROR":
                _logger.LogError(ex, "[RatingsCache] {Message}", message);
                break;
            case "WARN":
                _logger.LogWarning("[RatingsCache] {Message}", message);
                break;
            default:
                _logger.LogInformation("[RatingsCache] {Message}", message);
                break;
        }
    }
}

/// <summary>Dossier de données du plugin, stable d'une version à l'autre (le cache survit aux mises à jour).</summary>
public static class StorePaths
{
    public static string Dir(IApplicationPaths paths) => Path.Combine(paths.PluginConfigurationsPath, "JellyfinRatingsCache");
}
