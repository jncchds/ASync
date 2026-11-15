#!/usr/bin/env sh
# Prints the ASYNC_TEST_* variables for the containers of docker-compose.test.yml (project "async-test").
# Usage: eval "$(scripts/test-backends-env.sh)"
project="${1:-async-test}"
samba_ip=$(docker inspect -f '{{range .NetworkSettings.Networks}}{{.IPAddress}}{{end}}' "${project}-samba-1" 2>/dev/null)
echo "export ASYNC_TEST_S3=http://localhost:19000"
echo "export ASYNC_TEST_SFTP=localhost:12222"
echo "export ASYNC_TEST_WEBDAV=http://localhost:18081/"
echo "export ASYNC_TEST_POSTGRES='Host=localhost;Port=15432;Username=async;Password=async'"
[ -n "$samba_ip" ] && echo "export ASYNC_TEST_SMB=$samba_ip"
