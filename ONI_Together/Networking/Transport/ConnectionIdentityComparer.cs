using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace ONI_Together.Networking.Transport
{
    /// <summary>
    /// A reconnect to the same endpoint is a different connection. NetPeer inherits
    /// IPEndPoint.Equals, so default dictionaries can send a new handshake through an
    /// old, closed peer. Reference connections use identity; boxed Steam handles use
    /// value equality because each send may box the same handle again.
    /// </summary>
    internal sealed class ConnectionIdentityComparer : IEqualityComparer<object>
    {
        internal static readonly ConnectionIdentityComparer Instance = new ConnectionIdentityComparer();

        public new bool Equals(object a, object b) =>
            ReferenceEquals(a, b) || (a is ValueType && a.Equals(b));

        public int GetHashCode(object connection) => connection == null ? 0 :
            connection is ValueType ? connection.GetHashCode() : RuntimeHelpers.GetHashCode(connection);
    }
}
