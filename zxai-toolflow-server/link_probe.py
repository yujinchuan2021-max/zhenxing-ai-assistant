"""Conservative HTTPS HEAD checks for public download links.

The DNS answer is checked and the connection is pinned to that checked IP.  We
never use environment proxies, follow redirects, or download a response body.
"""

from __future__ import annotations

from dataclasses import dataclass
import http.client
import ipaddress
import re
import socket
import ssl
from urllib.parse import quote, urlsplit, urlunsplit


HOST_LABEL = re.compile(r"^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$")
BLOCKED_SUFFIXES = (".local", ".localhost", ".internal", ".lan", ".test", ".invalid")


class UnsafeLink(ValueError):
    pass


@dataclass(frozen=True)
class LinkCheck:
    status: str
    http_status: int | None = None


def normalize_public_https_url(value: str) -> str:
    if not isinstance(value, str) or not 1 <= len(value) <= 2048:
        raise UnsafeLink("URL must be a nonempty string of at most 2048 characters")
    if any(ord(char) < 32 or char == "\\" for char in value):
        raise UnsafeLink("URL contains a control character or backslash")
    try:
        parts = urlsplit(value)
        host = parts.hostname
        port = parts.port
    except (ValueError, UnicodeError) as exc:
        raise UnsafeLink("malformed URL") from exc
    if parts.scheme.lower() != "https" or not host or parts.username or parts.password:
        raise UnsafeLink("only HTTPS URLs without embedded credentials are allowed")
    if port not in (None, 443) or parts.fragment:
        raise UnsafeLink("custom ports and URL fragments are not allowed")
    if host.endswith(".") or "." not in host:
        raise UnsafeLink("a public DNS hostname is required")
    try:
        ascii_host = host.encode("idna").decode("ascii").lower()
    except UnicodeError as exc:
        raise UnsafeLink("invalid DNS hostname") from exc
    try:
        ipaddress.ip_address(ascii_host)
    except ValueError:
        pass  # Not an IP literal, as expected.
    else:
        raise UnsafeLink("IP literals are not allowed")
    if ascii_host.endswith(BLOCKED_SUFFIXES) or ascii_host == "localhost":
        raise UnsafeLink("local hostnames are not allowed")
    if len(ascii_host) > 253 or any(not HOST_LABEL.fullmatch(label) for label in ascii_host.split(".")):
        raise UnsafeLink("invalid DNS hostname")
    try:
        path = quote(parts.path or "/", safe="/%:@&=+$,;~()*!'-._")
        query = quote(parts.query, safe="/?%:@&=+$,;~()*!'-._")
    except UnicodeError as exc:
        raise UnsafeLink("URL contains invalid Unicode") from exc
    return urlunsplit(("https", ascii_host, path, query, ""))


def resolve_public_addresses(host: str, resolver=socket.getaddrinfo) -> list[str]:
    try:
        answers = resolver(host, 443, type=socket.SOCK_STREAM)
    except OSError as exc:
        raise UnsafeLink("DNS lookup failed") from exc
    addresses = list(dict.fromkeys(answer[4][0] for answer in answers))
    if not addresses:
        raise UnsafeLink("DNS returned no addresses")
    try:
        if any(not ipaddress.ip_address(address).is_global for address in addresses):
            raise UnsafeLink("DNS returned a non-public address")
    except ValueError as exc:
        raise UnsafeLink("DNS returned an invalid address") from exc
    return addresses


class _PinnedHTTPSConnection(http.client.HTTPSConnection):
    def __init__(self, host: str, address: str, timeout: float):
        super().__init__(host, port=443, timeout=timeout, context=ssl.create_default_context())
        self._checked_address = address

    def connect(self) -> None:
        raw = socket.create_connection((self._checked_address, 443), self.timeout)
        try:
            self.sock = self._context.wrap_socket(raw, server_hostname=self.host)
        except BaseException:
            raw.close()
            raise


def check_download_link(url: str, resolver=socket.getaddrinfo) -> LinkCheck:
    normalized = normalize_public_https_url(url)
    parts = urlsplit(normalized)
    try:
        addresses = resolve_public_addresses(parts.hostname or "", resolver)
        connection = _PinnedHTTPSConnection(parts.hostname or "", addresses[0], timeout=5)
        try:
            target = parts.path + ("?" + parts.query if parts.query else "")
            connection.request("HEAD", target, headers={"User-Agent": "ZhenxingToolflowLinkCheck/0.1"})
            code = connection.getresponse().status
        finally:
            connection.close()
    except (UnsafeLink, OSError, ssl.SSLError, http.client.HTTPException):
        return LinkCheck("unreachable")
    if 200 <= code < 300:
        return LinkCheck("available", code)
    if 300 <= code < 400:
        return LinkCheck("redirect_unchecked", code)
    if code in (405, 501):
        return LinkCheck("head_not_supported", code)
    if 400 <= code < 500:
        return LinkCheck("unavailable", code)
    return LinkCheck("server_error", code)
