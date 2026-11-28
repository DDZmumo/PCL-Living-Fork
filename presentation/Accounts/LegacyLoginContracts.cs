using System;
using System.Collections.Generic;
using System.Net.Http;

namespace PCLLF.Presentation.Accounts;

// LF f4aa0fae62c3ee8af24c81e61844c8fdd13c8983 Pages/PageLaunch login/skin/dialog
// sources are the behavior base; account preservation uses the current C022 APIs.
// PCL 5ef81e490ae46b1100d61e33d0183094c7a80ca8 is upstream. CE fixed
// 1a7653127d578efdb2a9388999a3fdc67d9e49d5 PageLoginMs.xaml.cs,
// PageLoginOffline.xaml.cs, PageLoginAuth.xaml.cs, PageLoginProfileSkin.xaml.cs,
// MySkin.xaml.cs were reviewed (their Plain Craft Launcher 2/LICENCE applies). Its profile store,
// UUID/new validators/localization/provider policy is not substituted for LF.
// Repository LF LICENCE applies. These hosts close the original DTO and UI types;
// no credentials, task state, account store, cache or scheduler is mirrored.
public enum LegacyLoginHint { Default, Red, Green, Blue }
