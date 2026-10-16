(function(){
  var d = document.documentElement;
  try { d.dataset.theme = localStorage.getItem('audio.theme') === 'light' ? 'light' : 'dark'; } catch(e){}
  try {
    var h = location.hash.replace(/^#/,'').replace(/-/g,'+').replace(/_/g,'/');
    if (h && atob(h).length === 16) d.classList.add('room');
  } catch(e){}
})();
