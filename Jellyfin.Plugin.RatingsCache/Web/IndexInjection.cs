using System;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.RatingsCache.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace Jellyfin.Plugin.RatingsCache.Web;

/// <summary>Compteurs affichés dans la page d'admin (preuve que l'injection fonctionne).</summary>
public sealed class InjectionState
{
    private long _count;

    public long Count => Interlocked.Read(ref _count);

    public DateTimeOffset? Last { get; private set; }

    public void Hit()
    {
        Interlocked.Increment(ref _count);
        Last = DateTimeOffset.UtcNow;
    }
}

/// <summary>Place le middleware d'injection en tête du pipeline HTTP de Jellyfin.</summary>
public sealed class IndexInjectionStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return app =>
        {
            app.UseMiddleware<IndexInjectionMiddleware>();
            next(app);
        };
    }
}

/// <summary>
/// Ajoute &lt;script src=".../JellyfinRatingsCache/client.js"&gt; à la page d'accueil du client web (index.html),
/// en mémoire, sans modifier aucun fichier de Jellyfin.
/// </summary>
public sealed class IndexInjectionMiddleware
{
    private const string Marker = "JellyfinRatingsCache/client.js";
    private static readonly Regex SafePrefix = new(@"^[A-Za-z0-9/_\-.~]*$", RegexOptions.Compiled);

    private readonly RequestDelegate _next;
    private readonly InjectionState _state;

    public IndexInjectionMiddleware(RequestDelegate next, InjectionState state)
    {
        _next = next;
        _state = state;
    }

    /// <summary>Retourne le préfixe d'URL (BaseUrl éventuel) si la requête vise la page d'accueil web, sinon null.</summary>
    public static string? MatchIndex(HttpRequest request)
    {
        if (!HttpMethods.IsGet(request.Method))
        {
            return null;
        }

        var path = request.PathBase.Value + request.Path.Value;
        foreach (var suffix in new[] { "/web/index.html", "/web/" })
        {
            if (path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                var prefix = path.Substring(0, path.Length - suffix.Length);
                return SafePrefix.IsMatch(prefix) ? prefix : null;
            }
        }

        return null;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var prefix = MatchIndex(context.Request);
        if (prefix == null || !Cfg.Current.InjectScript)
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        // On veut le HTML brut : ni compression, ni réponse 304, ni plage d'octets.
        context.Request.Headers.Remove("Accept-Encoding");
        context.Request.Headers.Remove("If-None-Match");
        context.Request.Headers.Remove("If-Modified-Since");
        context.Request.Headers.Remove("Range");

        var original = context.Response.Body;
        using var buffer = new MemoryStream();
        context.Response.Body = buffer;
        try
        {
            await _next(context).ConfigureAwait(false);
        }
        finally
        {
            context.Response.Body = original;
        }

        buffer.Position = 0;
        var isHtml = context.Response.StatusCode == StatusCodes.Status200OK &&
                     (context.Response.ContentType ?? string.Empty).StartsWith("text/html", StringComparison.OrdinalIgnoreCase) &&
                     !context.Response.Headers.ContainsKey("Content-Encoding");
        if (!isHtml)
        {
            await buffer.CopyToAsync(original, context.RequestAborted).ConfigureAwait(false);
            return;
        }

        var html = Encoding.UTF8.GetString(buffer.ToArray());
        if (!html.Contains(Marker, StringComparison.Ordinal))
        {
            var version = Plugin.Instance?.Version?.ToString() ?? "0";
            var tag = "<script src=\"" + WebUtility.HtmlEncode(prefix) + "/" + Marker + "?v=" + WebUtility.UrlEncode(version) + "\"></script>";
            var idx = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
            html = idx >= 0 ? html.Insert(idx, tag + "\n") : html + tag;
            _state.Hit();
        }

        var bytes = Encoding.UTF8.GetBytes(html);
        context.Response.ContentLength = bytes.Length;
        await original.WriteAsync(bytes, context.RequestAborted).ConfigureAwait(false);
    }
}
