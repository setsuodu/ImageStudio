# ImageStudio

图片 & 音视频处理中心（向 Convertio 风格扩展）。

## 功能：

### 图像
- 裁切 && 拼合
- 放大 && 缩小
- 转格式（PNG/JPG/WEBP/BMP）
- 网格切图（自动识别 / 去底色）
- 二维码生成

### 音视频（新增）
- **MP4 / 视频 / 音频 → MP3、WAV**（以及 FLAC / AAC / M4A / OGG / OPUS）
- 自动抽取音轨（去掉视频轨）
- 可调码率、采样率
- 接口：`POST /api/audio/convert?format=mp3|wav&bitrate=192`
- 状态：`GET /api/audio/status`（检查 ffmpeg 是否可用）

### FFmpeg 视频工具（专页）
- **视频裁剪**：去头/去尾/截取区间（对应 `-ss` / `-to` / `-t`，支持 `-c copy` 快速剪切）
- **修改视频码率**：默认 3000k，libx264 + 音频 copy
- **视频转序列帧**：按 fps 导出 PNG，打包 zip
- **水平等分**：取左半 / 右半 / 两侧（zip）
- 接口：
  - `POST /api/video/trim?start=46&end=120` 或 `?start=46&duration=74`
  - `POST /api/video/bitrate?bitrate=3000`
  - `POST /api/video/frames?fps=24&max=100`
  - `POST /api/video/split?side=left|right|both`
  - `GET /api/video/status`

## 平台

- Console
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
| POST | /api/audio/convert | **音视频 → MP3/WAV/...** |
| GET  | /api/audio/status | ffmpeg 与支持格式 |
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
