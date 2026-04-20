# BTCPay Server Plugin Template

A template for your own [BTCPay Server](https://github.com/btcpayserver) plugin.

Learn more in our [plugin documentation](https://docs.btcpayserver.org/Development/Plugins/).

## Development

The project references BTCPay Server via a git submodule. Initialize it before building:

```bash
git submodule update --init --recursive
```

Then run `./dev.sh` to start BTCPay Server with the plugin loaded.
