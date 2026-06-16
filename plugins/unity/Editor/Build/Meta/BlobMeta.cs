using System;
using System.Collections.Generic;

namespace Roulin.Editor.Build.Meta
{
    // Engine-tagged union. body_type discriminates which {engine}_body is set.
    [Serializable]
    public sealed class RoulinBlobMeta
    {
        public string blob_hash; // 64-char lower-hex
        public string body_type; // "unity" | "ue" | "godot"
        public RoulinUnityBlob unity_body;
        public RoulinUEBlob ue_body;
        public RoulinGodotBlob godot_body;

        public RoulinBlobMeta(RoulinUnityBlob bin, string blobHash)
        {
            this.blob_hash = blobHash;
            this.body_type = "unity";
            this.unity_body = bin;
        }
        
        public RoulinBlobMeta(RoulinUEBlob bin, string blobHash)
        {
            this.blob_hash = blobHash;
            this.body_type = "ue";
            this.ue_body = bin;
        }
        
        public RoulinBlobMeta(RoulinGodotBlob bin, string blobHash)
        {
            this.blob_hash = blobHash;
            this.body_type = "godot";
            this.godot_body = bin;
        }
    }

    [Serializable]
    public sealed class RoulinUnityBlob
    {
        public string unity_version;
        public string sbp_version;
        public string built_revision;
        public List<string> types = new();
        public List<RoulinUnityAsset> assets = new();
        public List<RoulinUnityScene> scenes = new();
    }

    [Serializable]
    public sealed class RoulinUnityAsset
    {
        public string guid; // 32-char lower-hex AssetDB GUID
        public string asset_address; // SBP AssetLoadInfo.address (Addressables address, or asset path fallback)
        public string asset_dependency_hash; // 32-char lower-hex Hash128
        public List<RoulinUnityObjectId> included_objects = new();
        public List<RoulinUnityObjectId> referenced_objects = new();
        public List<RoulinUnityObjectId> representations = new();
        public string build_usage_tag_set; // base64 of BuildUsageTagSet binary
        public List<RoulinUnityAssetHashEntry> referenced_asset_hashes = new();
    }

    [Serializable]
    public sealed class RoulinUnityAssetHashEntry
    {
        public string guid; // 32-char lower-hex
        public string asset_dependency_hash; // 32-char lower-hex Hash128
    }

    [Serializable]
    public sealed class RoulinUnityObjectId
    {
        public string guid; // 32-char lower-hex
        public long local_identifier_in_file;
        public int file_type; // SBP FileType enum
        public string file_path;

        // Indices into RoulinUnityBlob.types; empty = no type info, multi-Type allowed.
        public List<int> type_idxs = new();
    }

    [Serializable]
    public sealed class RoulinUnityScene
    {
        public string guid; // 32-char hex AssetDB GUID
        public string scene_path; // SceneDependencyInfo.m_Scene
        public List<RoulinUnityObjectId> referenced_objects = new();
        public List<int> included_type_idxs = new(); // indices into RoulinUnityBlob.types
        public RoulinUnityBuildUsageTagGlobal global_usage = new();
        public string build_usage_tag_set; // base64 of BuildUsageTagSet binary
        public string prefab_dependency_hash; // 32-char hex Hash128

        public List<RoulinUnityAssetHashEntry> referenced_asset_hashes = new();
    }

    // Name-addressed mirror of UnityEditor.Build.Content.BuildUsageTagGlobal:
    // forward-compatible with new Unity engine fields without schema churn.
    [Serializable]
    public sealed class RoulinUnityBuildUsageTagGlobal
    {
        public List<RoulinUsageGlobalUintField> uint_fields = new();
        public List<RoulinUsageGlobalBoolField> bool_fields = new();
    }

    [Serializable]
    public sealed class RoulinUsageGlobalUintField
    {
        public string name; // verbatim Unity field name (e.g. "m_LightmapModesUsed")
        public uint value;
    }


    [Serializable]
    public sealed class RoulinUsageGlobalBoolField
    {
        public string name;
        public bool value;
    }

    // Placeholders; populated when UE/Godot plugins land.
    [Serializable]
    public sealed class RoulinUEBlob
    {
        public string engine_version;
    }

    [Serializable]
    public sealed class RoulinGodotBlob
    {
        public string godot_version;
    }
}