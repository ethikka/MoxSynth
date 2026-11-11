#!/usr/bin/env sh
# Creates a self-signed HTTPS certificate for MoxSynth (no reverse proxy needed):
#
#   scripts/make-cert.sh [host name] [output folder]      defaults: moxsynth.anarki.local  ./data/certs
#
# Output:
#   moxsynth-ca.crt   your own root certificate – install and trust this on every computer / phone (see README)
#   moxsynth-ca.key   its private key – keep it, it is needed to renew the server certificate
#   moxsynth.crt      server certificate (+ CA), used by the container
#   moxsynth.key      server private key
#
# Running it again renews the server certificate with the existing CA, so devices don't need to be set up again.
# The server certificate is valid for 825 days (the longest Apple accepts), the CA for 10 years.
set -eu

HOST="${1:-moxsynth.anarki.local}"
OUT="${2:-./data/certs}"
SHORT="${HOST%%.*}"

mkdir -p "$OUT"
cd "$OUT"
umask 077

if [ ! -f moxsynth-ca.key ] || [ ! -f moxsynth-ca.crt ]; then
    echo "Creating CA (moxsynth-ca.crt)…"
    cat > ca.cnf <<EOF
[req]
distinguished_name = dn
x509_extensions = v3_ca
prompt = no
[dn]
CN = MoxSynth local CA
O = MoxSynth
[v3_ca]
basicConstraints = critical, CA:TRUE, pathlen:0
keyUsage = critical, keyCertSign, cRLSign
subjectKeyIdentifier = hash
EOF
    openssl req -x509 -new -nodes -newkey rsa:3072 -sha256 -days 3650 \
        -keyout moxsynth-ca.key -out moxsynth-ca.crt -config ca.cnf
    rm -f ca.cnf
fi

echo "Creating server certificate for $HOST…"
cat > server.cnf <<EOF
[req]
distinguished_name = dn
prompt = no
[dn]
CN = $HOST
[v3_server]
basicConstraints = critical, CA:FALSE
keyUsage = critical, digitalSignature, keyEncipherment
extendedKeyUsage = serverAuth
subjectKeyIdentifier = hash
authorityKeyIdentifier = keyid
subjectAltName = DNS:$HOST, DNS:$SHORT, DNS:localhost
EOF
openssl req -new -nodes -newkey rsa:2048 -sha256 -keyout moxsynth.key -out server.csr -config server.cnf
openssl x509 -req -in server.csr -CA moxsynth-ca.crt -CAkey moxsynth-ca.key -CAcreateserial \
    -sha256 -days 825 -extfile server.cnf -extensions v3_server -out server-only.crt
cat server-only.crt moxsynth-ca.crt > moxsynth.crt
rm -f server.cnf server.csr server-only.crt moxsynth-ca.srl

# The container user (PUID, default 1654) must be able to read the server certificate and key.
chmod 644 moxsynth.crt moxsynth-ca.crt
chmod 640 moxsynth.key

echo
echo "Done: $(pwd)"
openssl x509 -in moxsynth.crt -noout -subject -ext subjectAltName -enddate 2>/dev/null \
    || openssl x509 -in moxsynth.crt -noout -subject -enddate
echo
echo "Next: install and trust moxsynth-ca.crt on your devices, then: docker compose up -d"
