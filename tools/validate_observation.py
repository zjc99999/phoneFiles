#!/usr/bin/env python3
"""Observation 本地校验器：schema 结构 + 隐私红线 + 配置一致性。

零第三方依赖（不依赖 jsonschema），离线运行，适合社区贡献者在提交前自检。
用法:
    python tools/validate_observation.py <obs.json> [<obs2.json> ...]
    python tools/validate_observation.py --all contributions/observations/
退出码: 0=全部通过, 1=存在失败项。
"""
import json
import os
import re
import sys
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent

# --- schema 常量（与 rules/schema/observation.schema.json 对齐）---
ID_RE = re.compile(r"^obs_[0-9A-Z]{16,32}$")
SHA_RE = re.compile(r"^sha256:[0-9a-f]{64}$")
ROOTS = {"internal_shared", "sdcard1", "usb_otg", "unknown"}
STORAGE_TYPES = {"FIXED_ROM", "REMOVABLE_ROM", "FIXED_RAM", "REMOVABLE_RAM", "UNDEFINED", None}
LABELS = {"camera_media", "screenshots", "downloads", "recordings", "unknown"}
CHANNELS = {"mtp", "adb"}
TOP_REQUIRED = {"id", "device", "channel", "root", "entries", "privacy", "client", "submittedAt"}
DEVICE_REQUIRED = {"oem", "model", "osVersion", "sdk", "region"}
PRIVACY_REQUIRED = {"filenameSamples", "deviceIdHash", "consent"}
CLIENT_REQUIRED = {"appVersion", "os", "locale"}
DATETIME_RE = re.compile(r"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?(Z|[+-]\d{2}:\d{2})$")
# 禁止项：伪扩展名泄漏（.removed-20240201-192827 这类带日期时间戳的片段）
FAKE_EXT_RE = re.compile(r"removed-20\d{6}|20\d{6}-\d{6}")
# IMEI/序列号疑似模式（15-17 位纯数字串）
LONG_DIGITS_RE = re.compile(r"(?<!\d)\d{15,17}(?!\d)")
NODE_FORBIDDEN_RE = re.compile(r"[A-Za-z]:\\|[\\](?:Users|Documents and Settings)[\\]|@[0-9a-f]{16,}")


def check(cond, errors, msg):
    if not cond:
        errors.append(msg)


def validate_obs(data, raw_text, path, config=None):
    e = []
    check(isinstance(data, dict), e, "顶层不是对象")
    if not isinstance(data, dict):
        return e

    missing = TOP_REQUIRED - set(data)
    check(not missing, e, f"顶层缺字段: {sorted(missing)}")
    extra = set(data) - TOP_REQUIRED - {"app", "volume"}
    check(not extra, e, f"顶层多余字段: {sorted(extra)}")

    check(bool(ID_RE.match(str(data.get("id", "")))), e, f"id 格式非法: {data.get('id')}")

    dev = data.get("device")
    if isinstance(dev, dict):
        miss = DEVICE_REQUIRED - set(dev)
        check(not miss, e, f"device 缺字段: {sorted(miss)}")
        check(isinstance(dev.get("sdk"), int) and dev.get("sdk", 0) >= 1, e, "device.sdk 须为正整数")
        check(bool(dev.get("oem")) and bool(dev.get("model")), e, "device.oem/model 不得为空")
    else:
        e.append("device 不是对象")

    check(data.get("channel") in CHANNELS, e, f"channel 非法: {data.get('channel')}")
    check(data.get("root") in ROOTS, e, f"root 非法: {data.get('root')}")

    vol = data.get("volume")
    if vol is not None:
        if isinstance(vol, dict):
            check(bool(vol.get("displayName")), e, "volume.displayName 缺失")
            check(vol.get("storageType") in STORAGE_TYPES, e, f"volume.storageType 非法: {vol.get('storageType')}")
        else:
            e.append("volume 不是对象")

    entries = data.get("entries")
    if isinstance(entries, list) and len(entries) >= 1:
        for i, ent in enumerate(entries):
            if not isinstance(ent, dict):
                e.append(f"entries[{i}] 不是对象")
                continue
            check("nodePattern" in ent and "exists" in ent, e, f"entries[{i}] 缺 nodePattern/exists")
            np = ent.get("nodePattern", "")
            check(isinstance(np, str) and bool(np), e, f"entries[{i}].nodePattern 为空")
            check(not NODE_FORBIDDEN_RE.search(np), e, f"entries[{i}].nodePattern 含禁止模式(盘符/账号hash)")
            check(ent.get("exists") in (True, False), e, f"entries[{i}].exists 非布尔")
            if "canonicalLabel" in ent:
                check(ent["canonicalLabel"] in LABELS, e, f"entries[{i}].canonicalLabel 非法: {ent['canonicalLabel']}")
            fp = ent.get("fingerprint")
            if fp is not None:
                check(bool(re.match(r"^dir:[^|]*\|ext:\{.*\}$", fp)), e, f"entries[{i}].fingerprint 格式非法")
    else:
        e.append("entries 缺失或为空")

    priv = data.get("privacy")
    if isinstance(priv, dict):
        miss = PRIVACY_REQUIRED - set(priv)
        check(not miss, e, f"privacy 缺字段: {sorted(miss)}")
        check(priv.get("filenameSamples") is False, e, "★ privacy.filenameSamples 必须为 false")
        check(bool(SHA_RE.match(str(priv.get("deviceIdHash", "")))), e, "privacy.deviceIdHash 格式非法")
        check(bool(priv.get("consent")), e, "privacy.consent 为空")
    else:
        e.append("privacy 不是对象")

    cli = data.get("client")
    if isinstance(cli, dict):
        miss = CLIENT_REQUIRED - set(cli)
        check(not miss, e, f"client 缺字段: {sorted(miss)}")
    else:
        e.append("client 不是对象")

    check(bool(DATETIME_RE.match(str(data.get("submittedAt", "")))), e, f"submittedAt 格式非法: {data.get('submittedAt')}")

    # --- 隐私红线：扫描原始文本 ---
    check(not FAKE_EXT_RE.search(raw_text), e, "★ 原始文本含伪扩展名片段(文件名泄漏)")
    check(not LONG_DIGITS_RE.search(raw_text), e, "★ 原始文本含疑似 IMEI/序列号(15-17位数字串)")

    # --- 配置一致性（可选）---
    if config and isinstance(entries, list):
        target_patterns = {t["nodePattern"] for t in config.get("targets", [])}
        for ent in entries:
            np = ent.get("nodePattern", "")
            if np not in target_patterns:
                e.append(f"entries 中 {np!r} 不在 config/observe-config.json 的 targets 内（可能忘加配置）")

    return e


def main():
    args = sys.argv[1:]
    if not args:
        print(__doc__)
        return 2

    config = None
    cfg_path = REPO / "config" / "observe-config.json"
    if cfg_path.exists():
        config = json.loads(cfg_path.read_text(encoding="utf-8"))

    files = []
    if args[0] == "--all":
        root = Path(args[1]) if len(args) > 1 else REPO / "contributions" / "observations"
        files = sorted(root.rglob("obs_*.json")) if root.exists() else []
        if not files:
            print(f"未在 {root} 找到 obs_*.json")
            return 1
    else:
        files = [Path(a) for a in args]

    failed = 0
    for f in files:
        try:
            raw = f.read_text(encoding="utf-8")
            data = json.loads(raw)
        except Exception as ex:
            print(f"[FAIL] {f}: 无法读取/解析: {ex}")
            failed += 1
            continue
        errs = validate_obs(data, raw, f, config)
        if errs:
            failed += 1
            print(f"[FAIL] {f} ({len(errs)} 项):")
            for x in errs:
                print(f"    - {x}")
        else:
            n = len(data.get("entries", [])) if isinstance(data, dict) else 0
            print(f"[OK]   {f}  entries={n}")

    total = len(files)
    print(f"\n合计 {total} 份，通过 {total - failed}，失败 {failed}")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
