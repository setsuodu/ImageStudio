# ImageStudio

图片 & 音视频处理中心（向 Convertio 风格扩展）。

## 功能：

### 图像
- 裁切 && 拼合
- 放大 && 缩小
- 转格式（PNG/JPG/WEBP/BMP）
- 网格切图（自动识别 / 去底色）
- 二维码生成
- **PNG Prompt 提取**：上传 PNG，读取内嵌的 prompt / parameters（兼容 A1111、ComfyUI、NovelAI 等）

### 音视频（新增）
- **MP4 / 视频 / 音频 → MP3、WAV**（以及 FLAC / AAC / M4A / OGG / OPUS）
- 自动抽取音轨（去掉视频轨）
- 可调码率、采样率
- 接口：`POST /api/audio/convert?format=mp3|wav&bitrate=192`
- 状态：`GET /api/audio/status`（检查 ffmpeg 是否可用）

### FFmpeg 视频工具（专页）
- **视频转 MP4**：FLV / MKV / AVI / MOV 等 → MP4（优先 `-c copy` 只换容器，失败自动重编码）
- **视频裁剪**：去头/去尾/截取区间（对应 `-ss` / `-to` / `-t`，支持 `-c copy` 快速剪切）
- **修改视频码率**：默认 3000k，libx264 + 音频 copy
- **视频转序列帧**：按 fps 导出 PNG，打包 zip
- **水平等分**：取左半 / 右半 / 两侧（zip）
- 接口：
  - `POST /api/video/tomp4?crf=23`（可选 `&reencode=1` 强制重编码）
  - `POST /api/video/trim?start=46&end=120` 或 `?start=46&duration=74`
  - `POST /api/video/bitrate?bitrate=3000`
  - `POST /api/video/frames?fps=24&max=100`
  - `POST /api/video/split?side=left|right|both`
  - `GET /api/video/status`

## 平台

- Console（CLI + 本地 MCP）
- Web

## 编译

.net10 改👉成 .net10-alpine
v1.0 从 343 MB 减👉到 123 MB

> 说明： vs 启动项目切到docker，会多编译出一个 :dev标签的项目。 切到 Web 正常。

```bash
docker build -t imagecrop-web:latest -f src/ImageCrop.Web/Dockerfile .
```

镜像已内置 **ffmpeg**（Alpine apk），用于音视频转换。

## 部署

```bash
docker pull ghcr.io/setsuodu/imagestudio:latest

docker run -d \
  -p 8070:8070 \
  --name imagecrop-app \
  ghcr.io/setsuodu/imagestudio:latest
```

## API 速查

| 方法 | 路径 | 说明 |
|------|------|------|
| POST | /api/image/crop | 等比裁切 |
| POST | /api/image/scale | 最长边缩放 |
| POST | /api/image/convert | 图片格式转换 |
| GET  | /api/image/qrcode | 二维码 |
| POST | /api/image/grid | 网格切图 → zip |
| POST | /api/image/prompt | **PNG Prompt 提取**（返回 JSON：prompt / negative / parameters / texts） |
| POST | /api/audio/convert | **音视频 → MP3/WAV/...** |
| GET  | /api/audio/status | ffmpeg 与支持格式 |
| POST | /api/video/tomp4 | **任意视频 → MP4**（含 FLV→MP4） |
| POST | /api/video/trim | 视频裁剪（start/end/duration） |
| POST | /api/video/bitrate | 修改视频码率 |
| POST | /api/video/frames | 视频转序列帧 → zip |
| POST | /api/video/split | 水平等分 left/right/both |
| GET  | /api/video/status | 视频工具与 ffmpeg 状态 |

### 音视频转换示例

```bash
curl -F "file=@video.mp4" "http://localhost:8070/api/audio/convert?format=mp3&bitrate=192" -o out.mp3
curl -F "file=@video.mp4" "http://localhost:8070/api/audio/convert?format=wav" -o out.wav
```

---

## 本地 MCP（Claude / Cursor）

`ImageCrop.Console` 同时支持 CLI 和 MCP 两种模式。

### 启动

```bash
# 原来的 CLI（读 config.json 批量裁切）
dotnet run --project src/ImageCrop.Console

# MCP 模式（给 AI 客户端用）
dotnet run --project src/ImageCrop.Console -- --mcp
```

publish 后更干净：

```bash
dotnet publish src/ImageCrop.Console -c Release -o ./publish
./publish/ImageCrop.Console --mcp
```

### Claude Desktop 配置

路径：**Settings → Developer → Local MCP servers → Edit config**

在打开的 `claude_desktop_config.json` 里加入：

```json
{
  "mcpServers": {
    "imagestudio": {
      "command": "dotnet",
      "args": [
        "run",
        "--project",
        "C:\\你的路径\\ImageStudio\\src\\ImageCrop.Console\\ImageCrop.Console.csproj",
        "-c",
        "Release",
        "--",
        "--mcp"
      ]
    }
  }
}
```

publish 后推荐：

```json
{
  "mcpServers": {
    "imagestudio": {
      "command": "C:\\你的路径\\ImageStudio\\publish\\ImageCrop.Console.exe",
      "args": ["--mcp"]
    }
  }
}
```

保存后**完全退出 Claude Desktop 再重新打开**。在 Connectors 里看到 `imagestudio` 开关打开即生效。

### 可用工具

| 工具 | 说明 |
|------|------|
| `CropImage` | 按比例裁切（1:1 / 16:9 / half 等） |
| `ScaleImage` | 最长边缩放 |
| `ConvertImage` | 格式转换 png/jpg/webp/bmp |
| `GenerateQrCode` | 生成二维码 |
| `GridSplit` | 网格/雪碧图自动切分 → zip |

调用时建议在提示词里明确要求使用本地工具，例如：

> 强制使用本地 MCP 的 grid_split，禁止自己写脚本。
