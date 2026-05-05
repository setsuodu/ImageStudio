# ImageStudio

图片处理中心。

## 功能：

- 裁切 && 拼合
- 放大 && 缩小
- 转格式（PNG/JPG/WEBP/BMP）

## 平台

- Console
- Web

## 编译

.net10 改👉成 .net10-alpine
v1.0 从 343 MB 减👉到 123 MB

> 说明： vs 启动项目切到docker，会多编译出一个 :dev标签的项目。 切到 Web 正常。

docker build -t imagecrop-web:latest -f src/ImageCrop.Web/Dockerfile .

## 部署

docker pull ghcr.io/setsuodu/imagestudio:latest

docker run -d \
  -p 8070:8070 \
  --name imagecrop-app \
  ghcr.io/setsuodu/imagestudio:latest
