# 前端设计来源与独立实现声明

Open Agent Workbench 的前端与桌面壳层是本项目的独立实现。

## 参考范围

本项目在 2026 年 9 月研究了 PI-Desktop 的公开产品页面和公开截图，以理解通用的 Agent 桌面工作流，包括：项目/会话导航、居中任务入口、对话流、按需出现的工作面板、全局搜索和通知入口。

参考项目：<https://github.com/vastsa/PI-Desktop>

研究快照：`454715877750b4f944cc7b621432ea9bd461f321`。该提交号仅用于固定设计研究基线，不构成本项目的源码依赖。

PI-Desktop 在研究时声明采用 GNU Lesser General Public License v3.0。其许可证、版权和商标归原权利人所有。

## 没有复用的内容

- 未复制、翻译或改写 PI-Desktop 的源代码。
- 未引入其 React/TypeScript 组件、Rust 模块、样式表、图标、截图、文案或品牌资产。
- 未把 PI-Desktop 作为库链接、打包或随本项目分发。
- 本项目不声称与 PI-Desktop 或其维护者存在隶属、合作或背书关系。

## 本项目的实现

桌面壳层契约由本项目的 C#/.NET 原生 Host 生成；WebView2 只承担可替换的渲染工作。布局、状态模型、权限层、会话生命周期、后台任务和视觉令牌均依据 Open Agent Workbench 自身需求重新设计和实现。

由于没有复制或链接 PI-Desktop 的受 LGPL-3.0 保护代码，本项目自有代码继续按 MIT License 发布。若未来实际引入任何第三方代码或素材，必须在合并前记录来源、版本、许可证、修改情况和分发义务，并更新 `THIRD_PARTY_NOTICES.md`。
