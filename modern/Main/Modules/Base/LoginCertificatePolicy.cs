// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System;
using System.Linq;
using System.Net;
using MeloongCore.Wpf.Extensions;
using Microsoft.VisualBasic.CompilerServices;

namespace PCL
{
    internal sealed class LoginCertificatePolicy
    {
        private LoginCertificatePolicy()
        {
        }
        internal static bool Accept(object Sender, System.Net.Security.SslPolicyErrors Failure, bool ValidateLogin)
        {
            if (Failure == System.Net.Security.SslPolicyErrors.None)
                return true;
            if (ValidateLogin)
                return false;
            string Host = null;
            if (Sender is HttpWebRequest)
            {
                Host = ((HttpWebRequest)Sender).Address.DnsSafeHost;
            }
            else if (Sender is Uri)
            {
                Uri Address = (Uri)Sender;
                if (Address.IsAbsoluteUri && Address.Scheme == "https")
                    Host = Address.DnsSafeHost;
            }
            else if (Sender is string)
            {
                Host = Conversions.ToString(Sender);
            }
            if (string.IsNullOrWhiteSpace(Host))
                return false;
            Host = Host.TrimEnd('.');
            if (Uri.CheckHostName(Host) != UriHostNameType.Dns)
                return false;
            return new[] { "xboxlive.com", "minecraftservices.com" }.Any(Domain => Host.Equals(Domain, StringComparison.OrdinalIgnoreCase) || Host.EndsWith("." + Domain, StringComparison.OrdinalIgnoreCase));
        }
    }
}