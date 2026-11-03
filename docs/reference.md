---
hide:
  - toc
---

# OpenAPI reference

The contract of the REST endpoints, also available as a file:
[`openapi.yaml`](openapi.yaml) (OpenAPI 3.1), to generate a client in another
language or to import in Postman or Insomnia.

<div id="redoc-container"></div>
<script src="https://cdn.jsdelivr.net/npm/redoc@2.1.5/bundles/redoc.standalone.js"></script>
<script>
  Redoc.init("../openapi.yaml", {
    hideDownloadButton: false,
    expandResponses: "200",
    theme: { typography: { fontFamily: "inherit", headings: { fontFamily: "inherit" } } },
  }, document.getElementById("redoc-container"));
</script>
