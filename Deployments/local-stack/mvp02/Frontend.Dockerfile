FROM nginx@sha256:a8b39bd9cf0f83869a2162827a0caf6137ddf759d50a171451b335cecc87d236
COPY html/ /usr/share/nginx/html/
COPY mvp02-nginx.conf /etc/nginx/mvp02-nginx.conf
COPY mvp02-upstream.conf /etc/nginx/mvp02-upstream.conf
USER nginx
ENTRYPOINT ["nginx", "-c", "/etc/nginx/mvp02-nginx.conf", "-g", "daemon off;"]
