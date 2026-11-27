// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Threading;
using System.Threading.Tasks;
using ByteExplorer.Core.Models;

namespace ByteExplorer.Core.Services
{
    /// <summary>
    /// Owns settings.json. Current is always non-null and always usable
    /// — even before LoadAsync has run, even if the file is missing or
    /// corrupt — so nothing else in the app needs a null check before
    /// reading a setting.
    /// </summary>
    public interface ISettingsService
    {
        AppSettings Current { get; }

        Task LoadAsync(CancellationToken cancellationToken = default);

        Task SaveAsync(CancellationToken cancellationToken = default);
    }
}
