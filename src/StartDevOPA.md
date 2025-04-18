```ps
$Env:envCode="l";`
$Env:signingkey="verysecret";`
opa run -s -l info `
--set discovery.name=discovery,services.discovery.name=discovery,services.discovery.url=http://localhost:5035/api/opa,discovery.signing.keyid=globalkey,keys.globalkey.algorithm=HS256,keys.globalkey.key=$Env:signingkey,discovery.decision=ACME/discovery,labels.environmentCode=$Env:envCode


```
