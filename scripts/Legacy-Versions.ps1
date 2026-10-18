# The earlier releases the version switcher can run. Folder = src/legacy/<Folder>; Suffix = native DLL suffix.
# 1.3.0 and 1.4.0 have identical playback code (1.4.0 only added UI helpers to Core), so 1.4.0's source covers both.
$LegacyVersions = @(
    [pscustomobject]@{ Tag = "v1.2.0"; Folder = "V1_2_0"; Suffix = "v1_2_0" },
    [pscustomobject]@{ Tag = "v1.4.0"; Folder = "V1_4_0"; Suffix = "v1_4_0" },
    [pscustomobject]@{ Tag = "v1.4.1"; Folder = "V1_4_1"; Suffix = "v1_4_1" },
    [pscustomobject]@{ Tag = "v1.5.0"; Folder = "V1_5_0"; Suffix = "v1_5_0" },
    [pscustomobject]@{ Tag = "v1.6.0"; Folder = "V1_6_0"; Suffix = "v1_6_0" },
    [pscustomobject]@{ Tag = "v2.0.0"; Folder = "V2_0_0"; Suffix = "v2_0_0" },
    [pscustomobject]@{ Tag = "v2.0.1"; Folder = "V2_0_1"; Suffix = "v2_0_1" },
    [pscustomobject]@{ Tag = "v2.0.2"; Folder = "V2_0_2"; Suffix = "v2_0_2" }
)
