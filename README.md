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

> 说明： vs 启动项目切到docker，会多编译出一个 :dev标签的项目。 切到 Web 正常。

docker build -t imagecrop-web:latest -f src/ImageCrop.Web/Dockerfile .

## 部署

docker run -d \
  -p 8070:8070 \
  --name imagecrop-app \
  imagecrop-web:latest
