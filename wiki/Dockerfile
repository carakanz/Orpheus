FROM python:3.12-slim
WORKDIR /app
COPY index.html catalog.json server.py ./
ENV PYTHONUNBUFFERED=1
VOLUME ["/data"]
EXPOSE 8095
CMD ["python", "server.py", "--host", "0.0.0.0", "--port", "8095", "--data", "/data"]
