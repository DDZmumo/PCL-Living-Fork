// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, you can obtain one at https://mozilla.org/MPL/2.0/.
using System;
using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace PCLCS;

internal static class LegacyServerAddress {
    internal static bool TryParse(string address, out string host, out int port) {
        host = "";
        port = 25565;
        ReadOnlySpan<char> value = address.AsSpan().Trim();
        if (value.IsEmpty) return false;

        ReadOnlySpan<char> name;
        ReadOnlySpan<char> suffix;
        int colon = value.IndexOf(':');
        if (value[0] == '[') {
            int close = value.IndexOf(']');
            if (close < 2) return false;
            name = value[1..close];
            suffix = value[(close + 1)..];
            if (!IsIPv6(name)) return false;
        } else if (colon >= 0 && value[(colon + 1)..].Contains(':')) {
            // A port on an IPv6 literal requires brackets around the address.
            if (!IsIPv6(value)) return false;
            name = value;
            suffix = default;
        } else {
            name = colon < 0 ? value : value[..colon];
            suffix = colon < 0 ? default : value[colon..];
            if (Uri.CheckHostName(name.ToString()) is not (UriHostNameType.Dns or UriHostNameType.IPv4)) return false;
        }

        if (!suffix.IsEmpty && (suffix[0] != ':' ||
            !int.TryParse(suffix[1..], NumberStyles.None, CultureInfo.InvariantCulture, out port) ||
            port is < 1 or > 65535)) return false;
        host = name.ToString();
        return true;
    }

    private static bool IsIPv6(ReadOnlySpan<char> value) {
        int zone = value.IndexOf('%');
        if (zone >= 0) {
            ReadOnlySpan<char> scope = value[(zone + 1)..];
            if (scope.IsEmpty) return false;
            foreach (char character in scope) {
                if (!char.IsLetterOrDigit(character) && character is not ('_' or '-' or '.')) return false;
            }
            value = value[..zone];
        }
        return !value.Contains('[') && !value.Contains(']') &&
            IPAddress.TryParse(value, out IPAddress? address) && address.AddressFamily == AddressFamily.InterNetworkV6;
    }
}
