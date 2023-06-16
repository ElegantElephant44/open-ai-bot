Telegram bot that allows to communicate with OpenAI ChatGPT in a convenient way from the messenger.

### Env
ssh -i hetzner2 root@ip-address

### Variables
OPEN_AI_TOKEN

TELEGRAM_TOKEN

docker build -t open-ai-bot .
docker tag open-ai-bot:latest elegantelephant/open-ai-bot:latest
docker push elegantelephant/open-ai-bot:latest

docker run -d --restart unless-stopped -e TELEGRAM_TOKEN='' -e OPEN_AI_TOKEN='' --name bot elegantelephant/open-ai-bot:latest