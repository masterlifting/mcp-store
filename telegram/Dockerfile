FROM python:3.13-alpine

WORKDIR /app

ENV PYTHONDONTWRITEBYTECODE=1
ENV PYTHONUNBUFFERED=1

COPY requirements.txt ./
RUN pip install --no-cache-dir --upgrade pip
RUN pip install --no-cache-dir -r requirements.txt

COPY main.py sanitize.py ./
COPY telegram_mcp ./telegram_mcp

RUN adduser --disabled-password --gecos "" appuser && chown -R appuser:appuser /app
USER appuser

# Credentials come from the runtime environment, never from the image.
ENV TELEGRAM_API_ID=""
ENV TELEGRAM_API_HASH=""
ENV TELEGRAM_SESSION_NAME="telegram_mcp_session"
ENV TELEGRAM_SESSION_STRING=""

CMD ["python", "main.py"]
