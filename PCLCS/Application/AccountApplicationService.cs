// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System;
using System.Collections.Generic;

namespace PCLCS.Applications;

public interface IAccountApplicationService<TInput> where TInput : class {
    AccountApplicationSnapshot ReadSnapshot();
    string Validate(TInput input);
    bool SignIn(TInput input);
    void RequestCancellation();
    void SignOut(AccountProvider provider);
}

public sealed class AccountRuntimeState<TTask, TMicrosoft, TOffline, TServer>
    where TTask : class where TMicrosoft : class where TOffline : class where TServer : class {
    public TTask Login = null!;
    public TMicrosoft Microsoft = null!;
    public TOffline Offline = null!;
    public TServer Nide = null!, Auth = null!;
}

public sealed class AccountApplicationCommands<TInput> where TInput : class {
    public Action VerifyAccess = null!;
    public Func<AccountApplicationSnapshot> Read = null!;
    public Func<TInput, string> Validate = null!;
    public Func<bool> CanSignIn = null!;
    public Action<TInput> Start = null!;
    public Action Cancel = null!;
    public AccountSignOutHost SignOut = null!;
}

public sealed class AccountApplicationService<TInput, TTask, TMicrosoft, TOffline, TServer> : IAccountApplicationService<TInput>
    where TInput : class where TTask : class where TMicrosoft : class where TOffline : class where TServer : class {
    private readonly AccountApplicationCommands<TInput> commands;
    public AccountApplicationService(AccountApplicationCommands<TInput> commands) => this.commands = commands;
    [Newtonsoft.Json.JsonIgnore]
    public AccountRuntimeState<TTask, TMicrosoft, TOffline, TServer> Runtime { get; } = new();
    public AccountApplicationSnapshot ReadSnapshot() => commands.Read();
    public string Validate(TInput input) { commands.VerifyAccess(); return commands.Validate(input); }
    public bool SignIn(TInput input) {
        commands.VerifyAccess();
        if (!commands.CanSignIn() || !string.IsNullOrEmpty(commands.Validate(input))) return false;
        commands.Start(input); return true;
    }
    public void RequestCancellation() { commands.VerifyAccess(); commands.Cancel(); }
    public void SignOut(AccountProvider provider) { commands.VerifyAccess(); AccountSessionActions.SignOut(provider, commands.SignOut); }
}
