# SPDX-License-Identifier: MPL-2.0
# This Source Code Form is subject to the terms of the Mozilla Public License,
# v. 2.0. If a copy of the MPL was not distributed with this file, You can obtain
# one at https://mozilla.org/MPL/2.0/.
"""Build the repository index and the native about page's offline license text."""
import json
from pathlib import Path


def render(root: Path) -> tuple[str, str]:
    manifest = json.loads((root / "licenses/manifest.json").read_text(encoding="utf-8"))
    intro = (
        "PCL Living Fork 来源与许可\n"
        "PCLLF 原创独立代码：MPL-2.0，范围见 licenses/MPL-COVERED-FILES.txt。\n"
        "PCL、第三方代码、字体图标与其他资产保留各自许可；整个程序不统一改为 MPL。\n"
        "源码：https://github.com/DDZmumo/PCL-Living-Fork\n"
        "正式发行请按对应标签/提交取得源码。\n"
    )
    index = ["# 第三方来源与许可", "", intro.replace("\n", "\n\n").strip(), "", "| 组件 | 版本／范围 | 适用许可 |", "| --- | --- | --- |"]
    offline = [intro]
    for component in manifest["components"]:
        name = component["id"]
        label = component.get("version", component["scope"])
        texts = "、".join(f"[{Path(p).name}]({p})" for p in component["texts"])
        index.append(f"| [{name}]({component['source']}) | {label} | {component['license']}；{texts} |")
        offline.append(f"\n{name}\n{component['license']}\n{label}\n{component['scope']}\n来源：{component['source']}\n")
        if component.get("pending"):
            offline.append("待确认：" + component["pending"] + "\n")
    index += ["", "完整范围、核实方法与未确认项目见 [许可审计](docs/licensing-audit.md)。", "", "## 待核实的出处／授权", ""]
    offline.append("\n待核实的出处／授权\n")
    for item in manifest["unresolved"]:
        index.append(f"- **{item['id']}**：{item['reason']} 来源：{item['source']}")
        offline.append(f"\n{item['id']}\n{item['reason']}\n来源：{item['source']}\n")
    files = [manifest["originalLicenseText"], manifest["originalSourceList"], "NOTICE.md"]
    files += list(dict.fromkeys(path for component in manifest["components"] for path in component["texts"]))
    for name in files:
        offline += ["\n" + "=" * 72 + "\n" + name + "\n" + "=" * 72 + "\n", (root / name).read_text(encoding="utf-8-sig")]
    return "\n".join(index).rstrip() + "\n", "\n".join(offline).rstrip() + "\n"


if __name__ == "__main__":
    root = Path(__file__).resolve().parents[1]
    index, offline = render(root)
    (root / "THIRD-PARTY-NOTICES.md").write_text(index, encoding="utf-8")
    (root / "licenses/THIRD-PARTY-NOTICES.txt").write_text(offline, encoding="utf-8")
    print("Built third-party index and offline license text.")
