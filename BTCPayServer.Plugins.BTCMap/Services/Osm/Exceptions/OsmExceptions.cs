using System;

namespace BTCPayServer.Plugins.BTCMap.Services.Osm.Exceptions;

public class OsmException : Exception
{
    public int StatusCode { get; }
    public string Path { get; }
    public string RawBody { get; }

    public OsmException(int statusCode, string path, string message, string body = null) : base(message)
    {
        StatusCode = statusCode;
        Path = path;
        RawBody = body ?? string.Empty;
    }
}

/// <summary>401 from any OSM call. The merchant's access token is no longer valid.</summary>
public class OsmAuthException : OsmException
{
    public OsmAuthException(string path, string body) : base(401, path, $"OSM 401 {path}", body) { }
}

/// <summary>409 conflict from a node update — the OSM element changed since we fetched it.</summary>
public class OsmConflictException : OsmException
{
    public OsmConflictException(string path, string body) : base(409, path, $"OSM 409 {path}", body) { }
}

/// <summary>429 rate-limited by OSM (per OAuth app, not per IP).</summary>
public class OsmRateLimitException : OsmException
{
    public OsmRateLimitException(string path, string body) : base(429, path, $"OSM 429 {path}", body) { }
}

/// <summary>5xx from OSM — upstream failure.</summary>
public class OsmServerException : OsmException
{
    public OsmServerException(int status, string path, string body) : base(status, path, $"OSM {status} {path}", body) { }
}

/// <summary>The store has no OSM access token configured. Caller should prompt reconnect.</summary>
public class OsmNotConnectedException : OsmException
{
    public OsmNotConnectedException(string storeId)
        : base(0, "(none)", $"Store {storeId} has no OSM access token configured.") { }
}

/// <summary>OSM token endpoint returned an error (4xx). Body kept for diagnostics on <see cref="OsmException.RawBody"/>.</summary>
public class OsmTokenExchangeException : OsmException
{
    public string ErrorCode { get; }
    public string ErrorDescription { get; }

    public OsmTokenExchangeException(int status, string errorCode, string errorDescription, string body)
        : base(status, "/oauth2/token", $"OSM token exchange failed ({status}, {errorCode}): {errorDescription}", body)
    {
        ErrorCode = errorCode;
        ErrorDescription = errorDescription;
    }
}
