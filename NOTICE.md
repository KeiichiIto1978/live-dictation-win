# NOTICE

## original work（着想元）

本プロジェクトは、次の macOS アプリの設計思想を出発点としています。

- **live-dictation（文字起こしちゃん）**
- 作者: [minorun365](https://github.com/minorun365)
- リポジトリ: https://github.com/minorun365/live-dictation
- ライセンス: MIT License

「会議を検知して自動録音する」「マイクと再生音を別々に取り込んで話者を分ける」
「すべてローカルで完結させる」という設計は原作のものです。

原作は Apple のフレームワーク（ScreenCaptureKit / Speech / Translation /
FoundationModels）に依存しているため、本実装はコードを流用しておらず、
同じ思想を Windows の API で作り直した独立実装です。

原作のアイコンに使用されている「いらすとや」の素材は含みません。
本実装のトレイアイコンは実行時に描画しています。

## third party（依存ライブラリ）

| 名称 | 用途 | ライセンス |
|---|---|---|
| [NAudio](https://github.com/naudio/NAudio) | WASAPI による音声取り込み | MIT |
| [sherpa-onnx](https://github.com/k2-fsa/sherpa-onnx) | 音声認識・VAD の実行基盤 | Apache-2.0 |

## models（モデル）

モデルの重みは本リポジトリに含みません。利用時に各配布元から取得します。
ライセンスは配布元の条件に従ってください。

| 名称 | 用途 | 配布元 |
|---|---|---|
| nemo-parakeet-tdt_ctc-0.6b-ja | 日本語音声認識 | [csukuangfj](https://huggingface.co/csukuangfj/sherpa-onnx-nemo-parakeet-tdt_ctc-0.6b-ja-35000-int8) |
| silero VAD | 発話区間の検出 | [snakers4/silero-vad](https://github.com/snakers4/silero-vad) |
