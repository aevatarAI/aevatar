using System.Net.Sockets;

namespace Aevatar.Configuration;

/// <summary>Classifies transport failures that prove an HTTP connection was never established.</summary>
public static class NyxIdTransportFailureClassifier
{
    public static bool IsPreConnectFailure(HttpRequestException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        if (exception.HttpRequestError == HttpRequestError.NameResolutionError)
            return true;

        if (exception.HttpRequestError != HttpRequestError.ConnectionError)
            return false;

        for (Exception? current = exception.InnerException; current is not null; current = current.InnerException)
        {
            if (current is not SocketException socketException)
                continue;

            return socketException.SocketErrorCode is
                SocketError.ConnectionRefused or
                SocketError.NetworkDown or
                SocketError.NetworkUnreachable or
                SocketError.HostDown or
                SocketError.HostUnreachable or
                SocketError.AddressNotAvailable;
        }

        return false;
    }
}
